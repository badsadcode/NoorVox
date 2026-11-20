using System.Diagnostics;
using System.Security.Cryptography;
using NoorVox.Core.Backends;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Util;

namespace NoorVox.Core.Workers;

/// <summary>
/// Ownership record for one NoorVox-managed worker: <c>&lt;home&gt;/runtime/workers/&lt;id&gt;.json</c>.
/// PID + exact process start time guard against attaching to a recycled PID. Written by the supervisor;
/// deleted by the worker on clean exit (so a record whose process is gone means a crash).
/// </summary>
public sealed class WorkerRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string WorkerId { get; set; } = "";
    public int Pid { get; set; }
    public long ProcessStartTimeUtcTicks { get; set; }
    public string PipeName { get; set; } = "";
    public string Token { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public DateTimeOffset LaunchedAt { get; set; }
    public string LaunchedBy { get; set; } = "";
    /// <summary>True = in the launcher's kill-on-close job: the worker dies with its launcher by design.</summary>
    public bool BoundToLauncher { get; set; }
    public int LauncherPid { get; set; }
    public long LauncherStartTimeUtcTicks { get; set; }
    public int IdleTimeoutSeconds { get; set; }
}

public sealed record ResourceSnapshot(long WorkingSetBytes, long PrivateBytes, long CpuTimeMs, DateTimeOffset SampledAt, string Source, string Gpu);

/// <summary>Truthful status as seen by the supervisor (spec §7.3).</summary>
public sealed record WorkerStatusReport
{
    public required string WorkerId { get; init; }
    /// <summary><c>stopped</c> · <c>starting</c> · <c>idle</c> · <c>loading</c> · <c>ready</c> · <c>generating</c> · <c>stopping</c> · <c>error</c> · <c>unresponsive</c>.</summary>
    public required string State { get; init; }
    public bool Managed { get; init; } = true;
    public int? Pid { get; init; }
    public string? LaunchedBy { get; init; }
    public bool? BoundToLauncher { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public double? UptimeSeconds { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public string? Backend { get; init; }
    public string? BackendVersion { get; init; }
    public string? ExecutionProvider { get; init; }
    public LoadedModelInfo? LoadedModel { get; init; }
    public CurrentJobInfo? CurrentJob { get; init; }
    public long? JobsCompleted { get; init; }
    public ResourceSnapshot? Resources { get; init; }
    public int? IdleTimeoutSeconds { get; init; }
    public string? LogPath { get; init; }
    public string? Error { get; init; }
}

public sealed record StopReport(string WorkerId, bool WasRunning, string Method, int? Pid, string PreviousState, CurrentJobInfo? InterruptedJob, string? Note);

/// <summary>
/// Starts, inspects and stops NoorVox-managed worker processes. Works from short-lived CLI invocations (state lives in the
/// ownership registry + the worker itself) and long-lived hosts like the GUI (bound workers die with the host's job).
/// Never touches processes it did not launch.
/// </summary>
public sealed class WorkerSupervisor(StorageContext storage, string workerExePath, string launchedBy, JobObject? sharedJob = null) : IDisposable
{
    public const string WorkerProcessName = "NoorVox.Worker";
    public const string TokenEnvVar = "NOORVOX_WORKER_TOKEN";

    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StatusConnectTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan StatusResponseTimeout = TimeSpan.FromSeconds(3);

    JobObject? _job;

    public string WorkerExePath { get; } = workerExePath;

    public static string ValidateWorkerId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Invalid worker id '{id}'. Use 1-40 characters: letters, digits, '-' or '_'.");
        return id;
    }

    string RecordPath(string id) => Path.Combine(storage.WorkerRegistryDir, $"{ValidateWorkerId(id)}.json");

    WorkerRecord? ReadRecord(string id)
    {
        var path = RecordPath(id);
        if (!File.Exists(path)) return null;
        try { return Json.ReadFile<WorkerRecord>(path); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException) { return null; }
    }

    void DeleteRecord(string id)
    {
        try { File.Delete(RecordPath(id)); } catch (IOException) { }
    }

    /// <summary>The live process for a record, or null if it exited or the PID now belongs to someone else.</summary>
    static Process? OpenOwned(WorkerRecord rec)
    {
        try
        {
            var p = Process.GetProcessById(rec.Pid);
            if (p.HasExited || p.StartTime.ToUniversalTime().Ticks != rec.ProcessStartTimeUtcTicks
                || !p.ProcessName.Equals(WorkerProcessName, StringComparison.OrdinalIgnoreCase))
            {
                p.Dispose();
                return null;
            }
            return p;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    static bool LauncherAlive(WorkerRecord rec)
    {
        try
        {
            using var p = Process.GetProcessById(rec.LauncherPid);
            return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == rec.LauncherStartTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<string> KnownWorkerIds() =>
        Directory.Exists(storage.WorkerRegistryDir)
            ? Directory.EnumerateFiles(storage.WorkerRegistryDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToList()
            : [];

    public async Task<IReadOnlyList<WorkerStatusReport>> ListAsync(CancellationToken ct)
    {
        var list = new List<WorkerStatusReport>();
        foreach (var id in KnownWorkerIds())
        {
            var s = await GetStatusAsync(id, ct);
            if (s.State != "stopped") list.Add(s);
        }
        return list;
    }

    public async Task<WorkerStatusReport> GetStatusAsync(string id, CancellationToken ct)
    {
        var rec = ReadRecord(id);
        if (rec is null) return new WorkerStatusReport { WorkerId = id, State = "stopped" };

        using var proc = OpenOwned(rec);
        if (proc is null)
        {
            if (rec.BoundToLauncher && !LauncherAlive(rec))
            {
                DeleteRecord(id); // ended together with its launcher, as designed
                return new WorkerStatusReport { WorkerId = id, State = "stopped" };
            }
            return new WorkerStatusReport
            {
                WorkerId = id, State = "error", Pid = rec.Pid, LaunchedBy = rec.LaunchedBy, BoundToLauncher = rec.BoundToLauncher,
                LogPath = rec.LogPath,
                Error = "Worker process exited without a clean shutdown (crashed or killed externally). Run `noorvox workers stop` to clear it, or start it again.",
            };
        }

        try
        {
            var s = await WorkerClient.CallAsync<WorkerRuntimeStatus>(rec.PipeName, rec.Token, IpcMethods.Status, null,
                StatusConnectTimeout, StatusResponseTimeout, ct);
            return FromRuntime(rec, s);
        }
        catch (WorkerUnreachableException ex)
        {
            proc.Refresh();
            var age = DateTimeOffset.UtcNow - rec.LaunchedAt;
            return new WorkerStatusReport
            {
                WorkerId = id,
                State = age < StartTimeout ? "starting" : "unresponsive",
                Pid = rec.Pid, LaunchedBy = rec.LaunchedBy, BoundToLauncher = rec.BoundToLauncher,
                StartedAt = rec.LaunchedAt, UptimeSeconds = Math.Round(age.TotalSeconds, 1), LogPath = rec.LogPath,
                IdleTimeoutSeconds = rec.IdleTimeoutSeconds,
                Resources = new ResourceSnapshot(proc.WorkingSet64, proc.PrivateMemorySize64, (long)proc.TotalProcessorTime.TotalMilliseconds,
                    DateTimeOffset.UtcNow, "supervisor (System.Diagnostics.Process)", GpuNote(null)),
                Error = age < StartTimeout ? null : ex.Message,
            };
        }
    }

    static string GpuNote(string? provider) =>
        provider is null or "CPU" ? "unavailable: CPU execution provider, no GPU in use" : "unavailable: GPU metrics not implemented";

    static WorkerStatusReport FromRuntime(WorkerRecord rec, WorkerRuntimeStatus s) => new()
    {
        WorkerId = rec.WorkerId,
        State = s.State,
        Pid = s.Pid,
        LaunchedBy = rec.LaunchedBy,
        BoundToLauncher = rec.BoundToLauncher,
        StartedAt = s.StartedAt,
        UptimeSeconds = Math.Round((DateTimeOffset.UtcNow - s.StartedAt).TotalSeconds, 1),
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        Backend = s.Backend,
        BackendVersion = s.BackendVersion,
        ExecutionProvider = s.ExecutionProvider,
        LoadedModel = s.LoadedModel,
        CurrentJob = s.CurrentJob,
        JobsCompleted = s.JobsCompleted,
        Resources = new ResourceSnapshot(s.WorkingSetBytes, s.PrivateBytes, s.CpuTimeMs, s.SampledAt, "worker self-report (System.Diagnostics.Process)", GpuNote(s.ExecutionProvider)),
        IdleTimeoutSeconds = s.IdleTimeoutSeconds,
        LogPath = rec.LogPath,
        Error = s.LastError,
    };

    /// <summary>Cross-process, async-safe lock serialising start/stop of one worker id (prevents duplicate workers).</summary>
    async Task<IDisposable> LockAsync(string id, CancellationToken ct)
    {
        var dir = Path.Combine(storage.RuntimeDir, "locks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{ValidateWorkerId(id)}.lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(100, ct); }
            catch (IOException ex)
            {
                throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, $"Another NoorVox process is starting or stopping worker '{id}'.", null, ex);
            }
        }
    }

    /// <param name="boundToLauncher">Put the worker in this process's kill-on-close job, so it cannot outlive us.</param>
    public async Task<WorkerStatusReport> StartAsync(string id, bool boundToLauncher, int idleTimeoutSeconds, CancellationToken ct)
    {
        ValidateWorkerId(id);
        if (!File.Exists(WorkerExePath))
            throw new NoorVoxException(ErrorCodes.WorkerStartFailed, ExitCode.WorkerError, $"Worker executable not found: {WorkerExePath}", new { workerExePath = WorkerExePath });

        using var startStopLock = await LockAsync(id, ct);
        var existing = await GetStatusAsync(id, ct);
        if (existing.State is not ("stopped" or "error"))
            throw new NoorVoxException(ErrorCodes.WorkerAlreadyRunning, ExitCode.WorkerBusy,
                $"Worker '{id}' is already running (state {existing.State}, PID {existing.Pid}). NoorVox never starts duplicates.", existing);
        DeleteRecord(id);

        Directory.CreateDirectory(storage.WorkerRegistryDir);
        Directory.CreateDirectory(storage.LogsDir);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pipe = $"noorvox-worker-{id}-{Guid.NewGuid():N}";
        var log = Path.Combine(storage.LogsDir, $"worker-{id}.log");
        var recordPath = RecordPath(id);
        var args = $"--worker-id {id} --pipe {pipe} --record \"{recordPath}\" --log \"{log}\" --idle-timeout {Math.Max(0, idleTimeoutSeconds)}";

        if (boundToLauncher && sharedJob is null) _job ??= new JobObject();
        var job = sharedJob ?? _job;
        int pid;
        try
        {
            (pid, _) = NativeProcess.Start(WorkerExePath, args, Path.GetDirectoryName(WorkerExePath)!,
                new Dictionary<string, string> { [TokenEnvVar] = token }, breakawayFromJob: !boundToLauncher, boundToLauncher ? job : null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new NoorVoxException(ErrorCodes.WorkerStartFailed, ExitCode.WorkerError, $"Could not launch worker: {ex.Message}", null, ex);
        }

        using var proc = Process.GetProcessById(pid);
        using var self = Process.GetCurrentProcess();
        var rec = new WorkerRecord
        {
            WorkerId = id, Pid = pid, ProcessStartTimeUtcTicks = proc.StartTime.ToUniversalTime().Ticks,
            PipeName = pipe, Token = token, ExePath = WorkerExePath, LogPath = log,
            LaunchedAt = DateTimeOffset.UtcNow, LaunchedBy = launchedBy,
            BoundToLauncher = boundToLauncher, LauncherPid = self.Id, LauncherStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            IdleTimeoutSeconds = idleTimeoutSeconds,
        };
        FileUtil.WriteAllTextAtomic(recordPath, Json.Serialize(rec));

        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                DeleteRecord(id);
                throw new NoorVoxException(ErrorCodes.WorkerStartFailed, ExitCode.WorkerError,
                    $"Worker exited during startup (exit code {proc.ExitCode}). See {log}.", new { exitCode = proc.ExitCode, logPath = log, logTail = TailLog(log) });
            }
            try
            {
                var s = await WorkerClient.CallAsync<WorkerRuntimeStatus>(pipe, token, IpcMethods.Status, null,
                    TimeSpan.FromMilliseconds(500), StatusResponseTimeout, ct);
                return FromRuntime(rec, s);
            }
            catch (WorkerUnreachableException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
            catch (WorkerUnreachableException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                DeleteRecord(id);
                throw new NoorVoxException(ErrorCodes.WorkerStartFailed, ExitCode.WorkerError,
                    $"Worker did not become ready within {StartTimeout.TotalSeconds:0} s and was terminated. See {log}.", new { logPath = log, logTail = TailLog(log) });
            }
        }
    }

    static string? TailLog(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n');
            return string.Join('\n', lines.TakeLast(15)).Trim();
        }
        catch (IOException) { return null; }
    }

    async Task<(WorkerRecord Record, WorkerStatusReport Status)> RequireRunningAsync(string id, CancellationToken ct)
    {
        var status = await GetStatusAsync(id, ct);
        var rec = ReadRecord(id);
        if (rec is null || status.State is "stopped")
            throw new NoorVoxException(ErrorCodes.WorkerNotRunning, ExitCode.NotFound, $"Worker '{id}' is not running.", new { workerId = id, hint = $"noorvox workers start --worker {id}" });
        if (status.State is "error" or "unresponsive")
            throw new NoorVoxException(status.State == "error" ? ErrorCodes.WorkerError : ErrorCodes.WorkerUnresponsive, ExitCode.WorkerError,
                $"Worker '{id}' is {status.State}: {status.Error}", status);
        return (rec, status);
    }

    public async Task<WorkerStatusReport> LoadAsync(string id, InstalledModel model, CancellationToken ct)
    {
        var (rec, status) = await RequireRunningAsync(id, ct);
        if (status.LoadedModel is { } lm && lm.Directory != model.Directory)
        {
            // Another model is loaded: swapping in-process would not reclaim memory, so recycle (ADR-0001 decision 3).
            await RecycleAsync(id, ct);
            (rec, _) = await RequireRunningAsync(id, ct);
        }
        await WorkerClient.CallAsync<LoadedModelInfo>(rec.PipeName, rec.Token, IpcMethods.Load, new LoadParams(model.Directory),
            StatusConnectTimeout, TimeSpan.FromMinutes(5), ct);
        return await GetStatusAsync(id, ct);
    }

    /// <summary>"Unload": stop the worker and start a fresh one with the same id and binding, with no model loaded.</summary>
    public async Task<WorkerStatusReport> RecycleAsync(string id, CancellationToken ct)
    {
        var (rec, _) = await RequireRunningAsync(id, ct);
        await StopAsync(id, force: false, TimeSpan.FromSeconds(30), ct);
        return await StartAsync(id, rec.BoundToLauncher, rec.IdleTimeoutSeconds, ct);
    }

    public async Task<SynthesisResult> SynthesizeAsync(string id, SynthesizeParams p, CancellationToken ct)
    {
        var (rec, _) = await RequireRunningAsync(id, ct);
        try
        {
            return await WorkerClient.CallAsync<SynthesisResult>(rec.PipeName, rec.Token, IpcMethods.Synthesize, p, StatusConnectTimeout, null, ct);
        }
        catch (WorkerUnreachableException ex)
        {
            var after = await GetStatusAsync(id, CancellationToken.None);
            throw new NoorVoxException(ErrorCodes.WorkerError, ExitCode.WorkerError,
                $"Lost contact with worker '{id}' during synthesis (now {after.State}). No output was produced.", after, ex);
        }
    }

    public async Task<StopReport> StopAsync(string id, bool force, TimeSpan timeout, CancellationToken ct)
    {
        using var startStopLock = await LockAsync(id, ct);
        var rec = ReadRecord(id);
        if (rec is null) return new StopReport(id, false, "none", null, "stopped", null, "Worker was not running.");

        var status = await GetStatusAsync(id, ct);
        using var proc = OpenOwned(rec);
        if (proc is null)
        {
            DeleteRecord(id);
            return new StopReport(id, false, "cleared", rec.Pid, status.State, null,
                status.State == "error" ? "Worker had already exited unexpectedly; stale record cleared." : "Worker was not running.");
        }

        if (!force)
        {
            try
            {
                await WorkerClient.CallAsync<object>(rec.PipeName, rec.Token, IpcMethods.Shutdown, new ShutdownParams(Drain: true),
                    StatusConnectTimeout, StatusResponseTimeout, ct);
            }
            catch (WorkerUnreachableException) { /* fall through to waiting; it may already be exiting */ }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(wait.Token);
                DeleteRecord(id);
                return new StopReport(id, true, "graceful", rec.Pid, status.State, null, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NoorVoxException(ErrorCodes.WorkerStopTimeout, ExitCode.WorkerError,
                    $"Worker '{id}' did not exit within {timeout.TotalSeconds:0} s (state {status.State}). Use --force to terminate it.",
                    new { workerId = id, pid = rec.Pid, state = status.State, currentJob = status.CurrentJob });
            }
        }

        try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        DeleteRecord(id);
        return new StopReport(id, true, "forced", rec.Pid, status.State, status.CurrentJob,
            status.CurrentJob is null ? null : "An active job was interrupted; no output from it was kept.");
    }

    public async Task<IReadOnlyList<StopReport>> StopAllAsync(bool force, TimeSpan timeout, CancellationToken ct)
    {
        var results = new List<StopReport>();
        foreach (var id in KnownWorkerIds()) results.Add(await StopAsync(id, force, timeout, ct));
        return results;
    }

    /// <summary>
    /// Disposing closes this supervisor's own job handle: every worker bound to it is terminated by the OS.
    /// A <c>sharedJob</c> passed in by a long-lived host (the Studio) is owned and disposed by that host instead.
    /// </summary>
    public void Dispose() => _job?.Dispose();
}

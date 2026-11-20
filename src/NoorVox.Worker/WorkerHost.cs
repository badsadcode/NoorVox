using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Workers;

namespace NoorVox.Worker;

/// <summary>
/// Serves IPC requests on a current-user-only named pipe. Status requests are answered concurrently with synthesis;
/// synthesis itself is serialised. Exits on shutdown request or after the idle timeout, deleting its ownership record.
/// </summary>
public sealed class WorkerHost(string workerId, string pipeName, string token, string recordPath, int idleTimeoutSeconds,
    Func<ITtsBackend> backendFactory, WorkerLog log)
{
    const int MaxRequestChars = 4 * 1024 * 1024;
    readonly object _gate = new();
    readonly SemaphoreSlim _synthLock = new(1, 1);
    readonly CancellationTokenSource _shutdown = new();
    readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    readonly byte[] _tokenBytes = Encoding.UTF8.GetBytes(token);
    ITtsBackend? _backend;
    string _state = "idle";
    LoadedModelInfo? _loaded;
    CurrentJobInfo? _job;
    long _jobsCompleted;
    DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    string? _lastError;

    public async Task RunAsync()
    {
        _backend = backendFactory();
        log.Write($"started worker={workerId} pid={Environment.ProcessId} backend={_backend.BackendId} ({_backend.BackendVersion}) idleTimeout={idleTimeoutSeconds}s");
        var idleTask = IdleWatchAsync();
        var connections = new List<Task>();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await server.WaitForConnectionAsync(_shutdown.Token); }
                catch (OperationCanceledException) { await server.DisposeAsync(); break; }
                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(HandleAsync(server));
            }
        }
        finally
        {
            lock (_gate) _state = "stopping";
            log.Write("shutting down");
            await Task.WhenAny(Task.WhenAll(connections), Task.Delay(TimeSpan.FromSeconds(5)));
            await _synthLock.WaitAsync(TimeSpan.FromSeconds(30)); // let a draining job finish writing
            _backend?.Dispose();
            DeleteOwnRecord();
            log.Write("stopped cleanly");
        }
        await idleTask;
    }

    void DeleteOwnRecord()
    {
        try
        {
            if (!File.Exists(recordPath)) return;
            var rec = Json.ReadFile<WorkerRecord>(recordPath);
            if (rec.Pid == Environment.ProcessId) File.Delete(recordPath); // never delete a successor's record
        }
        catch (Exception ex) { log.Write($"could not remove record: {ex.Message}"); }
    }

    async Task IdleWatchAsync()
    {
        if (idleTimeoutSeconds <= 0) return;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, idleTimeoutSeconds)), _shutdown.Token);
                lock (_gate)
                {
                    if (_state is "idle" or "ready" && (DateTimeOffset.UtcNow - _lastActivity).TotalSeconds >= idleTimeoutSeconds)
                    {
                        log.Write($"idle for {idleTimeoutSeconds}s; exiting to free resources");
                        _shutdown.Cancel();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task HandleAsync(NamedPipeServerStream server)
    {
        await using var _ = server;
        var reader = new StreamReader(server, new UTF8Encoding(false));
        var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        IpcResponse response;
        string id = "";
        try
        {
            var line = await ReadLineLimitedAsync(reader);
            var req = line is null ? null : JsonSerializer.Deserialize<IpcRequest>(line, Json.Compact);
            if (req is null) return;
            id = req.Id;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Token), _tokenBytes))
            {
                log.Write("rejected request with invalid token");
                response = Fail(id, ErrorCodes.PermissionDenied, "Invalid worker token.");
            }
            else
            {
                var result = await DispatchAsync(req);
                response = new IpcResponse { Id = id, Ok = true, Result = JsonSerializer.SerializeToElement(result, Json.Compact) };
            }
        }
        catch (NoorVoxException ex)
        {
            response = Fail(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            log.Write($"request {id} failed: {ex}");
            response = Fail(id, ErrorCodes.WorkerError, ex.Message);
        }
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json.Compact)); }
        catch (IOException) { /* client went away */ }
    }

    static async Task<string?> ReadLineLimitedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        while (true)
        {
            int n = await reader.ReadAsync(buf);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            int nl = Array.IndexOf(buf, '\n', 0, n);
            sb.Append(buf, 0, nl >= 0 ? nl : n);
            if (nl >= 0) return sb.ToString();
            if (sb.Length > MaxRequestChars) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Request too large.");
        }
    }

    static IpcResponse Fail(string id, string code, string message) => new() { Id = id, Ok = false, Error = new IpcError(code, message) };

    async Task<object?> DispatchAsync(IpcRequest req)
    {
        switch (req.Method)
        {
            case IpcMethods.Status:
                return Snapshot();
            case IpcMethods.Load:
                return await LoadAsync(Params<LoadParams>(req));
            case IpcMethods.Synthesize:
                return await SynthesizeAsync(req.Id, Params<SynthesizeParams>(req));
            case IpcMethods.Shutdown:
                var p = req.Params is null ? new ShutdownParams() : Params<ShutdownParams>(req);
                log.Write($"shutdown requested (drain={p.Drain})");
                lock (_gate) _state = "stopping";
                _shutdown.Cancel();
                return new { accepted = true };
            default:
                throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown method '{req.Method}'.");
        }
    }

    static T Params<T>(IpcRequest req) =>
        req.Params is { } p ? p.Deserialize<T>(Json.Compact)! : throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Missing params.");

    WorkerRuntimeStatus Snapshot()
    {
        using var proc = Process.GetCurrentProcess();
        lock (_gate)
        {
            return new WorkerRuntimeStatus
            {
                WorkerId = workerId,
                Pid = proc.Id,
                State = _state,
                StartedAt = _startedAt,
                Backend = _backend?.BackendId ?? "",
                BackendVersion = _backend?.BackendVersion ?? "",
                ExecutionProvider = _backend?.ExecutionProvider ?? "",
                LoadedModel = _loaded,
                CurrentJob = _job,
                JobsCompleted = _jobsCompleted,
                LastActivityAt = _lastActivity,
                IdleTimeoutSeconds = idleTimeoutSeconds,
                WorkingSetBytes = proc.WorkingSet64,
                PrivateBytes = proc.PrivateMemorySize64,
                CpuTimeMs = (long)proc.TotalProcessorTime.TotalMilliseconds,
                SampledAt = DateTimeOffset.UtcNow,
                LastError = _lastError,
            };
        }
    }

    async Task<LoadedModelInfo> LoadAsync(LoadParams p)
    {
        var model = ModelRegistry.ReadPackage(p.ModelDirectory);
        lock (_gate)
        {
            if (_state == "stopping") throw new NoorVoxException(ErrorCodes.WorkerError, ExitCode.WorkerError, "Worker is stopping.");
            if (_loaded is not null)
            {
                if (_loaded.Directory == model.Directory) return _loaded; // idempotent
                throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, $"Worker already has {_loaded.Id}@{_loaded.Version} loaded; recycle it first.");
            }
            if (_state == "loading") throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, "A model is already loading.");
            _state = "loading";
            _lastActivity = DateTimeOffset.UtcNow;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            log.Write($"loading {model.Reference} from {model.Directory}");
            await _backend!.LoadAsync(model, _shutdown.Token);
            var info = new LoadedModelInfo(model.Id, model.Version, model.ModelSha256, model.Directory, DateTimeOffset.UtcNow, sw.ElapsedMilliseconds);
            lock (_gate) { _loaded = info; _state = "ready"; _lastActivity = DateTimeOffset.UtcNow; _lastError = null; }
            log.Write($"loaded in {sw.ElapsedMilliseconds} ms");
            return info;
        }
        catch (Exception ex)
        {
            lock (_gate) { _state = "idle"; _lastError = $"Model load failed: {ex.Message}"; }
            log.Write($"load failed: {ex}");
            throw new NoorVoxException(ErrorCodes.ModelLoadFailed, ExitCode.WorkerError, $"Could not load {model.Reference}: {ex.Message}");
        }
    }


    async Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeParams p)
    {
        lock (_gate)
        {
            if (_loaded is null) throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "No model loaded in this worker.");
            if (_state == "stopping") throw new NoorVoxException(ErrorCodes.WorkerError, ExitCode.WorkerError, "Worker is stopping.");
        }
        if (!p.Wait && _synthLock.CurrentCount == 0)
            throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, "Worker is busy generating.");
        var outDir = Path.GetDirectoryName(Path.GetFullPath(p.OutputPath));
        if (outDir is null || !Directory.Exists(outDir))
            throw new NoorVoxException(ErrorCodes.IoError, ExitCode.IoError, $"Output directory does not exist: {outDir}");

        await _synthLock.WaitAsync(_shutdown.Token);
        try
        {
            lock (_gate)
            {
                _state = "generating";
                _job = new CurrentJobInfo(requestId, "synthesize", DateTimeOffset.UtcNow, p.Text.Length, p.VoiceId);
                _lastActivity = DateTimeOffset.UtcNow;
            }
            log.Write($"synthesize req={requestId} chars={p.Text.Length} voice={p.VoiceId} speed={p.Speed}");
            var result = await _backend!.SynthesizeAsync(new SynthesisRequest(p.Text, p.VoiceId, p.Speed), p.OutputPath, _shutdown.Token);
            lock (_gate) _jobsCompleted++;
            log.Write($"done req={requestId} audio={result.DurationMs} ms in {result.ElapsedMs} ms");
            return result;
        }
        catch (Exception ex) when (ex is not NoorVoxException)
        {
            try { File.Delete(p.OutputPath); } catch (IOException) { }
            lock (_gate) _lastError = $"Synthesis failed: {ex.Message}";
            log.Write($"synthesis failed: {ex}");
            throw new NoorVoxException(ErrorCodes.SynthesisFailed, ExitCode.SynthesisFailed, $"Synthesis failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _job = null;
                if (_state == "generating") _state = "ready";
                _lastActivity = DateTimeOffset.UtcNow;
            }
            _synthLock.Release();
        }
    }
}

public sealed class WorkerLog : IDisposable
{
    readonly StreamWriter _writer;
    readonly object _lock = new();

    public WorkerLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
    }

    public void Write(string message)
    {
        lock (_lock) _writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}");
    }

    public void Dispose() => _writer.Dispose();
}

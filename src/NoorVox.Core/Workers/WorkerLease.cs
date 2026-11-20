using NoorVox.Core.Models;
using NoorVox.Core.Storage;

namespace NoorVox.Core.Workers;

/// <summary>How a caller wants a worker for some synthesis work.</summary>
public sealed record WorkerPolicy
{
    /// <summary>CLI: explicit worker id (else reuse a warm <c>default</c> or use a one-shot worker).</summary>
    public string? WorkerId { get; init; }
    /// <summary>CLI: leave the worker running afterwards.</summary>
    public bool KeepWorker { get; init; }
    /// <summary>Studio: always use <see cref="WorkerId"/> (default <c>studio</c>); start it if needed; never stop it afterwards.</summary>
    public bool Dedicated { get; init; }
    public bool DedicatedBoundToLauncher { get; init; } = true;
    public int DedicatedIdleTimeoutSeconds { get; init; }
}

/// <summary>
/// A worker with the right model loaded, for the duration of some work. Disposing stops it only if this lease started a
/// one-shot worker; warm/persistent/dedicated workers are left running. Shared by quick narration and batch jobs.
/// </summary>
public sealed class WorkerLease : IAsyncDisposable
{
    public const string DefaultWorker = "default";
    readonly WorkerSupervisor _supervisor;
    public string Id { get; }
    public bool Reused { get; }
    public bool Started { get; }
    public bool StopsOnDispose { get; }
    public bool Stopped { get; private set; }

    WorkerLease(WorkerSupervisor supervisor, string id, bool reused, bool started, bool stopsOnDispose)
    {
        _supervisor = supervisor;
        Id = id;
        Reused = reused;
        Started = started;
        StopsOnDispose = stopsOnDispose;
    }

    public static async Task<WorkerLease> AcquireAsync(StorageContext storage, WorkerSupervisor supervisor, InstalledModel model,
        WorkerPolicy policy, Action<string>? progress, CancellationToken ct)
    {
        if (policy.Dedicated)
        {
            var dedicated = WorkerSupervisor.ValidateWorkerId(policy.WorkerId ?? "studio");
            var ds = await supervisor.GetStatusAsync(dedicated, ct);
            bool startedNow = false;
            if (ds.State is "stopped" or "error")
            {
                progress?.Invoke("Starting voice engine...");
                ds = await supervisor.StartAsync(dedicated, policy.DedicatedBoundToLauncher, policy.DedicatedIdleTimeoutSeconds, ct);
                startedNow = true;
            }
            if (ds.LoadedModel?.Directory != model.Directory)
            {
                progress?.Invoke($"Loading {model.Manifest.DisplayName}...");
                await supervisor.LoadAsync(dedicated, model, ct);
            }
            return new WorkerLease(supervisor, dedicated, !startedNow, startedNow, stopsOnDispose: false);
        }

        bool keep = policy.KeepWorker;
        var id = WorkerSupervisor.ValidateWorkerId(policy.WorkerId ?? DefaultWorker);
        var st = await supervisor.GetStatusAsync(id, ct);

        if (st.State is "ready" or "generating" && st.LoadedModel?.Directory == model.Directory)
            return new WorkerLease(supervisor, id, true, false, false);
        if (st.State is "idle")
        {
            progress?.Invoke($"Loading {model.Reference} into worker '{id}'...");
            await supervisor.LoadAsync(id, model, ct);
            return new WorkerLease(supervisor, id, true, false, false);
        }
        if (policy.WorkerId is not null && st.State is not ("stopped" or "error"))
            throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy,
                $"Worker '{id}' is {st.State} with {(st.LoadedModel is null ? "no model" : st.LoadedModel.Id + "@" + st.LoadedModel.Version)}; it can't serve {model.Reference} now.", st);

        if (st.State is "stopped" or "error" && (policy.WorkerId is not null || keep))
        {
            progress?.Invoke($"Starting worker '{id}'...");
            await supervisor.StartAsync(id, boundToLauncher: !keep, keep ? storage.Settings.Workers.IdleTimeoutSeconds : 0, ct);
        }
        else
        {
            // A persistent 'default' worker serves another model (or is busy loading): use a private one-shot worker.
            id = $"synth-{Environment.ProcessId}";
            keep = false;
            progress?.Invoke($"Starting one-shot worker '{id}'...");
            await supervisor.StartAsync(id, boundToLauncher: true, 0, ct);
        }
        try
        {
            progress?.Invoke($"Loading {model.Reference}...");
            await supervisor.LoadAsync(id, model, ct);
        }
        catch
        {
            if (!keep) await SafeStopAsync(supervisor, id);
            throw;
        }
        return new WorkerLease(supervisor, id, false, true, stopsOnDispose: !keep);
    }

    public async ValueTask DisposeAsync()
    {
        if (StopsOnDispose && !Stopped)
        {
            await SafeStopAsync(_supervisor, Id);
            Stopped = true;
        }
    }

    static async Task SafeStopAsync(WorkerSupervisor sup, string id)
    {
        try { await sup.StopAsync(id, force: false, TimeSpan.FromSeconds(15), CancellationToken.None); }
        catch (NoorVoxException) { await sup.StopAsync(id, force: true, TimeSpan.FromSeconds(5), CancellationToken.None); }
    }
}

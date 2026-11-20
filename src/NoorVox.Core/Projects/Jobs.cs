using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;
using NoorVox.Core.Util;

namespace NoorVox.Core.Projects;

public static class JobStates
{
    public const string Queued = "queued", Running = "running", Completed = "completed", Cancelled = "cancelled", Failed = "failed", Interrupted = "interrupted";
}

public static class ItemStates
{
    public const string Queued = "queued", Running = "running", Generated = "generated", Reused = "reused", Skipped = "skipped", Failed = "failed", Interrupted = "interrupted";
    public static bool IsDone(string s) => s is Generated or Reused or Skipped;
}

public sealed class JobScope
{
    public string? SectionId { get; set; }
    public string? SceneId { get; set; }
    public List<string>? SegmentIds { get; set; }
}

/// <summary><c>.noorvox/jobs/&lt;jobId&gt;.json</c> — persisted before execution and after every item (spec §10.2).</summary>
public sealed class GenerationJob
{
    public int SchemaVersion { get; set; } = 1;
    public string JobId { get; set; } = "";
    public string Kind { get; set; } = "generate";
    public JobScope Scope { get; set; } = new();
    /// <summary><c>missing</c>: generate only where no take matches the current settings (reuse others). <c>all</c>: always a new take.</summary>
    public string Policy { get; set; } = "missing";
    /// <summary>Approve the resulting take for segments that have no approved take. Never replaces an existing approval.</summary>
    public bool AutoApprove { get; set; }
    public string State { get; set; } = JobStates.Queued;
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<JobItem> Items { get; set; } = [];
    public string? Error { get; set; }

    public int Total => Items.Count;
    public int Done => Items.Count(i => ItemStates.IsDone(i.State));
    public int FailedCount => Items.Count(i => i.State == ItemStates.Failed);
    public int Remaining => Items.Count(i => !ItemStates.IsDone(i.State) && i.State != ItemStates.Failed);
}

public sealed class JobItem
{
    public string SegmentId { get; set; } = "";
    public string State { get; set; } = ItemStates.Queued;
    public string? TakeId { get; set; }
    public long? DurationMs { get; set; }
    public long? SynthesisMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed record JobProgress(string JobId, string State, int Done, int Total, int Failed, string? CurrentSegmentId, JobItem? LastItem);

/// <summary>Synthesizes text to a WAV at <c>outputWav</c> and returns measured facts. Production: a worker; tests: a fake.</summary>
public delegate Task<SynthesisResult> SegmentSynthesizer(string text, string voiceId, double speed, string outputWav, CancellationToken ct);

/// <summary>
/// Batch generation that survives interruption. Each finished item is committed (WAV moved into place, take JSON written,
/// job file updated) before the next starts, so a crash or Ctrl+C loses at most the item in flight; <see cref="RunAsync"/>
/// on the same job resumes at the first unfinished item. Successful takes are never deleted because a later item fails.
/// </summary>
public sealed class JobRunner(ProjectStore store, InstalledModel model)
{
    string JobPath(string id) => Path.Combine(store.JobsDir, $"{id}.json");

    public GenerationJob Create(JobScope scope, string policy, bool autoApprove)
    {
        if (policy is not ("missing" or "all"))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Policy must be 'missing' or 'all'.");
        var segments = store.AllSegments().Where(l =>
            (scope.SectionId is null || l.Section.SectionId == scope.SectionId) &&
            (scope.SceneId is null || l.Scene.SceneId == scope.SceneId) &&
            (scope.SegmentIds is null || scope.SegmentIds.Contains(l.Segment.SegmentId))).ToList();
        if (scope.SegmentIds is not null)
            foreach (var id in scope.SegmentIds.Where(id => segments.All(s => s.Segment.SegmentId != id)))
                throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No segment '{id}' in this project.");
        if (scope.SectionId is not null && store.Project.Sections.All(s => s.SectionId != scope.SectionId))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No section '{scope.SectionId}' in this project.");

        var job = new GenerationJob
        {
            JobId = $"job_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}",
            Scope = scope,
            Policy = policy,
            AutoApprove = autoApprove,
            Model = model.Reference,
            CreatedAt = DateTimeOffset.Now,
            Items = segments.Select(l => new JobItem { SegmentId = l.Segment.SegmentId }).ToList(),
        };
        Save(job);
        return job;
    }

    void Save(GenerationJob job)
    {
        Directory.CreateDirectory(store.JobsDir);
        FileUtil.WriteAllTextAtomic(JobPath(job.JobId), Json.Serialize(job));
    }

    public GenerationJob Load(string jobId)
    {
        if (!ProjectStore.IsValidId(jobId) || !File.Exists(JobPath(jobId)))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No job '{jobId}' in this project.");
        var job = Json.ReadFile<GenerationJob>(JobPath(jobId));
        return RecoverIfStale(job);
    }

    /// <summary>All jobs, newest first. A job left "running" by a process that no longer exists becomes "interrupted".</summary>
    public IReadOnlyList<GenerationJob> List()
    {
        if (!Directory.Exists(store.JobsDir)) return [];
        return Directory.EnumerateFiles(store.JobsDir, "job_*.json")
            .Select(f => { try { return RecoverIfStale(Json.ReadFile<GenerationJob>(f)); } catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { return null; } })
            .Where(j => j is not null).Select(j => j!)
            .OrderByDescending(j => j.CreatedAt).ToList();
    }

    GenerationJob RecoverIfStale(GenerationJob job)
    {
        if (job.State != JobStates.Running) return job;
        using var probe = TryLock();
        if (probe is null) return job; // a live runner holds the lock: it really is running
        job.State = JobStates.Interrupted;
        foreach (var i in job.Items.Where(i => i.State == ItemStates.Running)) i.State = ItemStates.Interrupted;
        Save(job);
        return job;
    }

    FileStream? TryLock()
    {
        Directory.CreateDirectory(store.StateDir);
        try { return new FileStream(Path.Combine(store.StateDir, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { return null; }
    }

    /// <param name="stopAfterCurrent">Polled between items: a soft "pause after this segment".</param>
    public async Task<GenerationJob> RunAsync(GenerationJob job, SegmentSynthesizer synth, IProgress<JobProgress>? progress,
        Func<bool>? stopAfterCurrent, CancellationToken ct)
    {
        using var jobLock = TryLock() ?? throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy,
            "Another generation job is already running for this project.");
        CleanTemp();

        job.State = JobStates.Running;
        job.StartedAt ??= DateTimeOffset.Now;
        job.FinishedAt = null;
        job.Error = null;
        foreach (var i in job.Items.Where(i => i.State is ItemStates.Running or ItemStates.Interrupted or ItemStates.Failed)) i.State = ItemStates.Queued;
        Save(job);
        var pron = store.Pronunciation();

        foreach (var item in job.Items)
        {
            if (ItemStates.IsDone(item.State)) continue;
            if (stopAfterCurrent?.Invoke() == true)
            {
                job.State = JobStates.Interrupted;
                break;
            }
            item.State = ItemStates.Running;
            item.Error = null;
            Save(job);
            progress?.Report(new JobProgress(job.JobId, job.State, job.Done, job.Total, job.FailedCount, item.SegmentId, null));

            try
            {
                await RunItemAsync(job, item, synth, pron, ct);
            }
            catch (OperationCanceledException)
            {
                item.State = ItemStates.Interrupted;
                job.State = JobStates.Interrupted;
                job.FinishedAt = DateTimeOffset.Now;
                Save(job);
                progress?.Report(new JobProgress(job.JobId, job.State, job.Done, job.Total, job.FailedCount, null, item));
                throw;
            }
            catch (Exception ex)
            {
                item.State = ItemStates.Failed;
                item.Error = ex.Message;
            }
            item.FinishedAt = DateTimeOffset.Now;
            Save(job);
            progress?.Report(new JobProgress(job.JobId, job.State, job.Done, job.Total, job.FailedCount, null, item));
        }

        if (job.State == JobStates.Running)
            job.State = job.FailedCount > 0 && job.Done == 0 ? JobStates.Failed : JobStates.Completed;
        job.FinishedAt = DateTimeOffset.Now;
        Save(job);
        progress?.Report(new JobProgress(job.JobId, job.State, job.Done, job.Total, job.FailedCount, null, null));
        return job;
    }

    async Task RunItemAsync(GenerationJob job, JobItem item, SegmentSynthesizer synth, PronunciationFile pron, CancellationToken ct)
    {
        store.Reload();
        var loc = store.AllSegments().FirstOrDefault(l => l.Segment.SegmentId == item.SegmentId);
        if (loc is null || string.IsNullOrWhiteSpace(loc.Segment.Text))
        {
            item.State = ItemStates.Skipped;
            item.Error = loc is null ? "Segment was removed from the script." : "Segment has no text.";
            return;
        }
        var seg = loc.Segment;
        var eff = store.Effective(seg, model, pron);
        if (!model.Voices.Any(v => v.VoiceId == eff.VoiceId))
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{eff.VoiceId}' is not installed.");
        var takes = store.Takes(seg.SegmentId);

        TakeRecord? take = job.Policy == "missing" ? takes.LastOrDefault(t => t.CacheKey == eff.CacheKey) : null;
        if (take is not null)
        {
            item.State = ItemStates.Reused;
        }
        else
        {
            take = await GenerateTakeAsync(job, seg, eff, synth, ct);
            item.State = ItemStates.Generated;
        }
        item.TakeId = take.TakeId;
        item.DurationMs = take.DurationMs;

        if (job.AutoApprove && seg.ApprovedTakeId is null)
            store.UpdateSegment(seg.SegmentId, s => s.ApprovedTakeId ??= take.TakeId); // never replaces an approval made meanwhile
    }

    async Task<TakeRecord> GenerateTakeAsync(GenerationJob job, Segment seg, EffectiveSegment eff, SegmentSynthesizer synth, CancellationToken ct)
    {
        Directory.CreateDirectory(store.TempDir);
        var tmp = Path.Combine(store.TempDir, $"{Guid.NewGuid():N}.wav");
        try
        {
            var r = await synth(eff.SpokenText, eff.VoiceId, eff.Speed, tmp, ct);
            var info = WavInfo.Read(tmp); // validate before committing
            var takeId = $"t{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
            var rel = $"takes/{seg.SegmentId}/{takeId}.wav";
            var dest = store.SafePath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(tmp, dest);
            var record = new TakeRecord
            {
                TakeId = takeId, SegmentId = seg.SegmentId, CacheKey = eff.CacheKey, SpokenText = eff.SpokenText,
                SourceTextSha256 = FileUtil.Sha256Text(seg.Text), VoiceId = eff.VoiceId, Speed = eff.Speed,
                ModelId = model.Id, ModelVersion = model.Version, ModelSha256 = model.ModelSha256, Backend = model.Manifest.Backend ?? "kokoro-onnx",
                File = rel, DurationMs = info.DurationMs, SampleRate = info.SampleRate, Samples = info.Samples,
                CreatedAt = DateTimeOffset.Now, JobId = job.JobId, Generator = $"NoorVox {NoorVoxInfo.Version}",
            };
            // The JSON is the commit marker: written last, atomically.
            FileUtil.WriteAllTextAtomic(store.SafePath($"takes/{seg.SegmentId}/{takeId}.json"), Json.Serialize(record));
            return record;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(store.TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(store.TempDir)) File.Delete(f); // we hold the job lock: nothing else writes here
        }
        catch (IOException) { }
    }
}

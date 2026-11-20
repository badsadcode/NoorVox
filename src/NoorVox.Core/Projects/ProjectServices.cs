using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Core.Projects;

/// <summary>Runs project jobs on a real NoorVox worker (CLI and studio share this).</summary>
public static class ProjectGeneration
{
    /// <summary>The project's model, or NoorVox's default model when the project doesn't pin one.</summary>
    public static InstalledModel ResolveModel(StorageContext storage, ProjectStore project) =>
        new ModelRegistry(storage).Resolve(project.Project.Defaults.Model);

    public static async Task<GenerationJob> RunAsync(StorageContext storage, WorkerSupervisor supervisor, ProjectStore project, InstalledModel model,
        GenerationJob job, WorkerPolicy policy, IProgress<JobProgress>? progress, Action<string>? status, Func<bool>? stopAfterCurrent, CancellationToken ct)
    {
        if (job.Remaining == 0 && job.FailedCount == 0) return job;
        await using var lease = await WorkerLease.AcquireAsync(storage, supervisor, model, policy, status, ct);
        SegmentSynthesizer synth = (text, voice, speed, style, output, c) =>
            supervisor.SynthesizeAsync(lease.Id, new SynthesizeParams(text, voice, speed, output, Style: style), c);
        return await new JobRunner(project, model).RunAsync(job, synth, progress, stopAfterCurrent, ct);
    }
}

public sealed record SegmentView(
    string SegmentId, string Text, string? SpeakerId, string? VoiceOverride, double? SpeedOverride, string EffectiveVoice, double EffectiveSpeed,
    string SpokenText, string Status, int Takes, string? ApprovedTakeId, string? LatestTakeId, long? DurationMs, string? Notes,
    string? StyleOverride = null, string? EffectiveStyle = null);

public sealed record SceneView(string SceneId, string Title, IReadOnlyList<SegmentView> Segments);
public sealed record SectionView(string SectionId, string Title, IReadOnlyList<SceneView> Scenes, IReadOnlyDictionary<string, int> Counts);

public sealed record ProjectOverview(
    string Root, ProjectFile Project, ProjectKind Kind, string? Model, IReadOnlyList<SectionView> Sections,
    IReadOnlyDictionary<string, int> Counts, long ApprovedDurationMs, int SegmentCount, IReadOnlyList<GenerationJob> RecentJobs,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Snapshot of a project with every segment's computed status. Needs the model to compute cache keys; without an
    /// installed model every segment with text reports <c>missing</c> and a warning says why.
    /// </summary>
    public static ProjectOverview Build(ProjectStore store, InstalledModel? model, bool includeText = true)
    {
        store.Reload();
        var pron = store.Pronunciation();
        var warnings = new List<string>();
        if (model is null) warnings.Add("No voice model installed: statuses can't be computed until one is installed.");
        var all = new Dictionary<string, int>();
        long approvedMs = 0;
        int count = 0;
        var sections = new List<SectionView>();
        foreach (var file in store.LoadSections())
        {
            var counts = new Dictionary<string, int>();
            var scenes = new List<SceneView>();
            foreach (var sc in file.Scenes)
            {
                var segs = new List<SegmentView>();
                foreach (var seg in sc.Segments)
                {
                    count++;
                    var takes = store.Takes(seg.SegmentId);
                    EffectiveSegment? eff = model is null ? null : store.Effective(seg, model, pron);
                    var status = string.IsNullOrWhiteSpace(seg.Text) ? SegmentStatus.Empty
                        : eff is null ? SegmentStatus.Missing : store.Status(seg, eff, takes);
                    var approved = takes.FirstOrDefault(t => t.TakeId == seg.ApprovedTakeId);
                    if (seg.ApprovedTakeId is not null && approved is null) warnings.Add($"{seg.SegmentId}: approved take {seg.ApprovedTakeId} is missing on disk.");
                    if (approved is not null) approvedMs += approved.DurationMs;
                    if (model is not null && eff is not null && !model.HasVoice(eff.VoiceId))
                        warnings.Add($"{seg.SegmentId}: voice '{eff.VoiceId}' is not installed.");
                    counts[status] = counts.GetValueOrDefault(status) + 1;
                    all[status] = all.GetValueOrDefault(status) + 1;
                    segs.Add(new SegmentView(seg.SegmentId, includeText ? seg.Text : "", seg.SpeakerId, seg.VoiceId, seg.Speed,
                        eff?.VoiceId ?? "", eff?.Speed ?? store.Project.Defaults.Speed, includeText ? eff?.SpokenText ?? seg.Text : "",
                        status, takes.Count, seg.ApprovedTakeId, takes.LastOrDefault()?.TakeId, (approved ?? takes.LastOrDefault())?.DurationMs, seg.Notes,
                        seg.Style, eff?.Style));
                }
                scenes.Add(new SceneView(sc.SceneId, sc.Title, segs));
            }
            sections.Add(new SectionView(file.SectionId, file.Title, scenes, counts));
        }
        var jobs = model is null ? [] : new JobRunner(store, model).List().Take(5).ToList();
        return new ProjectOverview(store.Root, store.Project, store.Kind, model?.Reference, sections, all, approvedMs, count, jobs,
            warnings.Distinct().Take(50).ToList());
    }

    /// <summary>Structural checks used by <c>projects validate</c>.</summary>
    public static IReadOnlyList<string> Validate(ProjectStore store, InstalledModel? model)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in store.Project.Sections)
        {
            if (!File.Exists(store.SafePath(r.File))) { problems.Add($"Section '{r.SectionId}': file {r.File} is missing."); continue; }
            var f = store.LoadSection(r.SectionId);
            if (f.SectionId != r.SectionId) problems.Add($"Section file {r.File} says '{f.SectionId}', project says '{r.SectionId}'.");
            foreach (var seg in f.Scenes.SelectMany(s => s.Segments))
            {
                if (!ProjectStore.IsValidId(seg.SegmentId)) problems.Add($"Invalid segment id '{seg.SegmentId}'.");
                if (!ids.Add(seg.SegmentId)) problems.Add($"Duplicate segment id '{seg.SegmentId}'.");
                if (seg.SpeakerId is not null && store.Project.Speakers.All(s => s.SpeakerId != seg.SpeakerId))
                    problems.Add($"{seg.SegmentId}: unknown speaker '{seg.SpeakerId}'.");
                if (seg.Speed is { } sp && (model is null ? sp is < 0.5 or > 2.0
                        : model.Capabilities.Speed ? sp < model.Capabilities.MinSpeed || sp > model.Capabilities.MaxSpeed : Math.Abs(sp - 1.0) > 1e-9))
                    problems.Add(model?.Capabilities.Speed == false
                        ? $"{seg.SegmentId}: speed {sp}, but {model.Manifest.DisplayName} has no speed control."
                        : $"{seg.SegmentId}: speed {sp} outside the model's range.");
                if (seg.ApprovedTakeId is not null && store.Takes(seg.SegmentId).All(t => t.TakeId != seg.ApprovedTakeId))
                    problems.Add($"{seg.SegmentId}: approved take {seg.ApprovedTakeId} not found.");
            }
        }
        if (model is not null)
        {
            var voices = model.Voices.Select(v => v.VoiceId).ToHashSet();
            foreach (var v in store.Project.Speakers.Select(s => s.VoiceId).Append(store.Project.Defaults.VoiceId).Where(v => v is not null).Distinct())
                if (!voices.Contains(v!)) problems.Add($"Voice '{v}' is used by the project but not installed for {model.Reference}.");
        }
        return problems;
    }
}

using NoorVox.Core.Models;
using NoorVox.Core.Util;

namespace NoorVox.Core.Projects;

public sealed record ClipInfo(
    string SegmentId, string SectionId, string SceneId, string File, long DurationMs, int SampleRate,
    string Text, string? Speaker, string VoiceId, string TakeId, bool Stale);

public sealed record ClipExportResult(string OutputDir, string ManifestPath, IReadOnlyList<ClipInfo> Clips, IReadOnlyList<string> Missing, IReadOnlyList<string> Stale);

/// <summary>
/// Copies approved takes out of a project with stable, id-based file names (spec §5.3: names from ids, not text),
/// plus a <c>clips.json</c> listing every file with its measured duration, text and speaker.
/// Full episode/chapter assembly with pauses and subtitles is Phase 4.
/// </summary>
public static class ClipExporter
{
    public static ClipExportResult Export(ProjectStore store, InstalledModel model, string? sectionId, string outDir, bool allowMissing, bool overwrite)
    {
        var scope = store.AllSegments().Where(l => sectionId is null || l.Section.SectionId == sectionId).Where(l => !string.IsNullOrWhiteSpace(l.Segment.Text)).ToList();
        if (sectionId is not null && store.Project.Sections.All(s => s.SectionId != sectionId))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No section '{sectionId}' in this project.");
        var pron = store.Pronunciation();
        var missing = new List<string>();
        var stale = new List<string>();
        var plan = new List<(SegmentLocation Loc, TakeRecord Take, bool Stale)>();
        foreach (var l in scope)
        {
            var take = l.Segment.ApprovedTakeId is { } id ? store.Takes(l.Segment.SegmentId).FirstOrDefault(t => t.TakeId == id) : null;
            if (take is null) { missing.Add(l.Segment.SegmentId); continue; }
            bool isStale = take.CacheKey != store.Effective(l.Segment, model, pron).CacheKey;
            if (isStale) stale.Add(l.Segment.SegmentId);
            plan.Add((l, take, isStale));
        }
        if (missing.Count > 0 && !allowMissing)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput,
                $"{missing.Count} segment(s) have no approved take. Approve them, generate with auto-approve, or allow missing clips.",
                new { missing = missing.Take(50), total = missing.Count });
        if (plan.Count == 0)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Nothing to export: no approved takes in this scope.");

        var dir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(dir);
        bool dialogue = store.Kind.Id == "dialogue";
        var names = plan.Select((p, i) => dialogue ? $"{p.Loc.Segment.SegmentId}.wav" : $"{i + 1:000}_{p.Loc.Segment.SegmentId}.wav").ToList();
        var clash = names.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        if (clash.Count > 0 && !overwrite)
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"{clash.Count} file(s) already exist in {dir}. Choose an empty folder or allow overwriting.", new { examples = clash.Take(5) });

        var clips = new List<ClipInfo>();
        for (int i = 0; i < plan.Count; i++)
        {
            var (loc, take, isStale) = plan[i];
            var target = Path.Combine(dir, names[i]);
            File.Copy(store.SafePath(take.File), target, overwrite);
            var speaker = loc.Segment.SpeakerId is null ? null : store.Project.Speakers.FirstOrDefault(s => s.SpeakerId == loc.Segment.SpeakerId)?.Name;
            clips.Add(new ClipInfo(loc.Segment.SegmentId, loc.Section.SectionId, loc.Scene.SceneId, target, take.DurationMs, take.SampleRate,
                loc.Segment.Text, speaker, take.VoiceId, take.TakeId, isStale));
        }
        var manifestPath = Path.Combine(dir, "clips.json");
        FileUtil.WriteAllTextAtomic(manifestPath, Json.Serialize(new
        {
            schemaVersion = 1,
            project = new { store.Project.ProjectId, store.Project.Title, store.Project.Kind, root = store.Root },
            section = sectionId,
            exportedAt = DateTimeOffset.Now,
            generator = $"NoorVox {NoorVoxInfo.Version}",
            totalDurationMs = clips.Sum(c => c.DurationMs),
            clips,
            missing,
            stale,
        }));
        return new ClipExportResult(dir, manifestPath, clips, missing, stale);
    }
}

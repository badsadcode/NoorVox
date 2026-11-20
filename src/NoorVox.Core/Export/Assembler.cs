using System.Globalization;
using System.Text;
using NoorVox.Core.Audio;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;
using NoorVox.Core.Util;

namespace NoorVox.Core.Export;

public sealed record AssemblyOptions
{
    /// <summary>Output sample rate; null keeps the model's native rate (24 kHz for Kokoro).</summary>
    public int? SampleRate { get; init; }
    /// <summary>Scale the whole export so its peak sits at <see cref="PeakDbfs"/>. Export-only; takes are never modified.</summary>
    public bool NormalizePeak { get; init; }
    public double PeakDbfs { get; init; } = -1.0;
    /// <summary>Skip segments without an approved take instead of failing.</summary>
    public bool AllowMissing { get; init; }
    public bool Overwrite { get; init; }
    public bool Subtitles { get; init; } = true;
    public int SubtitleLineChars { get; init; } = 42;
}

public sealed record AssembledSegment(
    int Index, string SegmentId, string SceneId, string SceneTitle, string? Speaker, string VoiceId,
    string Text, string SpokenText, string? Notes, long StartMs, long EndMs, long DurationMs,
    long StartSample, long EndSample, string TakeId, string TakeFile, bool Stale);

public sealed record AssembledScene(string SceneId, string Title, long StartMs, long EndMs);

public sealed record AssemblyAudio(string File, int SampleRate, int Channels, int BitsPerSample, long Samples, long DurationMs, long Bytes, string Sha256);

public sealed record AssemblyProcessing(int SourceSampleRate, bool Resampled, string Resampler, bool Normalized, double? GainDb, double PeakDbfsAfter);

public sealed record AssemblyPauses(int BetweenSegmentsMs, int BetweenScenesMs, int SectionEdgeMs, string Rule);

/// <summary>
/// The timing manifest for video assembly (spec §11.3). Every time is measured in samples of the written file and
/// reported in milliseconds. Schema: docs/schemas/assembly-manifest.schema.json.
/// </summary>
public sealed record AssemblyManifest(
    int SchemaVersion, string Kind, string Generator, DateTimeOffset CreatedAt,
    object Project, object Section, AssemblyAudio Audio, object? Subtitles, AssemblyPauses Pauses, AssemblyProcessing Processing,
    IReadOnlyList<AssembledScene> Scenes, IReadOnlyList<AssembledSegment> Segments,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Stale, string Timing);

public sealed record AssemblyResult(string ManifestPath, string AudioPath, string? SrtPath, string? VttPath, AssemblyManifest Manifest);

/// <summary>
/// Joins a section's approved takes into one WAV with structured pauses, and writes SRT/VTT captions plus a timing
/// manifest. Takes are read, never changed. Durations and offsets come from sample counts, never from text estimates.
/// </summary>
public static class Assembler
{
    public const string PauseRule =
        "Gap between two segments = betweenSegmentsMs (betweenScenesMs across a scene boundary) unless either side sets " +
        "pauseAfterMs/pauseBeforeMs, then the larger explicit value. sectionEdgeMs of silence at start and end.";

    sealed record Planned(SegmentLocation Loc, TakeRecord Take, bool Stale, PcmBuffer Audio, int GapBeforeSamples);

    public static AssemblyResult AssembleSection(ProjectStore store, InstalledModel model, string sectionId, string outDir, AssemblyOptions options)
    {
        var section = store.Project.Sections.FirstOrDefault(s => s.SectionId == sectionId)
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No section '{sectionId}' in this project.");
        var pron = store.Pronunciation();
        var pauses = store.Project.Pauses;
        var locs = store.AllSegments().Where(l => l.Section.SectionId == sectionId && !string.IsNullOrWhiteSpace(l.Segment.Text)).ToList();

        var missing = new List<string>();
        var stale = new List<string>();
        var picked = new List<(SegmentLocation Loc, TakeRecord Take, bool Stale)>();
        foreach (var l in locs)
        {
            var take = l.Segment.ApprovedTakeId is { } id ? store.Takes(l.Segment.SegmentId).FirstOrDefault(t => t.TakeId == id) : null;
            if (take is null) { missing.Add(l.Segment.SegmentId); continue; }
            bool isStale = take.CacheKey != store.Effective(l.Segment, model, pron).CacheKey;
            if (isStale) stale.Add(l.Segment.SegmentId);
            picked.Add((l, take, isStale));
        }
        if (missing.Count > 0 && !options.AllowMissing)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput,
                $"{missing.Count} segment(s) in '{section.Title}' have no approved take. Approve them, or allow missing segments to leave them out.",
                new { missing = missing.Take(50), total = missing.Count });
        if (picked.Count == 0)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"'{section.Title}' has no approved takes to assemble.");

        // Load audio and plan the gaps at the takes' native rate.
        var planned = new List<Planned>();
        int? nativeRate = null;
        for (int i = 0; i < picked.Count; i++)
        {
            var (loc, take, isStale) = picked[i];
            var audio = PcmBuffer.ReadWav(store.SafePath(take.File));
            nativeRate ??= audio.SampleRate;
            if (audio.SampleRate != nativeRate)
                throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput,
                    $"Take {take.TakeId} is {audio.SampleRate} Hz but earlier takes are {nativeRate} Hz; mixed rates can't be assembled yet.");
            int gapMs;
            if (i == 0) gapMs = pauses.SectionEdgeMs;
            else
            {
                var prev = picked[i - 1].Loc;
                gapMs = prev.Scene.SceneId != loc.Scene.SceneId ? pauses.BetweenScenesMs : pauses.BetweenSegmentsMs;
                int? explicitGap = new[] { prev.Segment.PauseAfterMs, loc.Segment.PauseBeforeMs }.Where(v => v is not null).Max();
                if (explicitGap is { } eg) gapMs = eg;
            }
            planned.Add(new Planned(loc, take, isStale, audio, (int)Math.Round(Math.Max(0, gapMs) * (long)nativeRate.Value / 1000.0)));
        }
        int rate = nativeRate!.Value;
        int tail = (int)Math.Round(Math.Max(0, pauses.SectionEdgeMs) * (long)rate / 1000.0);

        long total = planned.Sum(p => (long)p.GapBeforeSamples + p.Audio.Samples.LongLength) + tail;
        var mix = new float[total];
        var segments = new List<AssembledSegment>();
        long pos = 0;
        foreach (var (p, index) in planned.Select((p, i) => (p, i)))
        {
            pos += p.GapBeforeSamples;
            Array.Copy(p.Audio.Samples, 0, mix, pos, p.Audio.Samples.LongLength);
            long start = pos, end = pos + p.Audio.Samples.LongLength;
            pos = end;
            var seg = p.Loc.Segment;
            var speaker = seg.SpeakerId is null ? null : store.Project.Speakers.FirstOrDefault(s => s.SpeakerId == seg.SpeakerId)?.Name;
            segments.Add(new AssembledSegment(index + 1, seg.SegmentId, p.Loc.Scene.SceneId, p.Loc.Scene.Title, speaker, p.Take.VoiceId,
                seg.Text, p.Take.SpokenText, seg.Notes, Ms(start, rate), Ms(end, rate), Ms(end - start, rate), start, end,
                p.Take.TakeId, p.Take.File, p.Stale));
        }

        // Export processing (on the copy in memory only).
        var buffer = new PcmBuffer(mix, rate);
        int outRate = options.SampleRate ?? rate;
        bool resampled = outRate != rate;
        if (resampled) buffer = Resampler.Resample(buffer, outRate);
        double? gainDb = null;
        if (options.NormalizePeak)
        {
            var peak = buffer.Peak();
            if (peak > 0)
            {
                var target = (float)Math.Pow(10, options.PeakDbfs / 20.0);
                var g = target / peak;
                for (int i = 0; i < buffer.Samples.Length; i++) buffer.Samples[i] *= g;
                gainDb = Math.Round(20 * Math.Log10(g), 2);
            }
        }
        double ratio = (double)outRate / rate;
        if (resampled)
            segments = segments.Select(s => s with { StartSample = (long)Math.Round(s.StartSample * ratio), EndSample = (long)Math.Round(s.EndSample * ratio) }).ToList();

        // Write outputs (temp + move; never overwrite unless asked).
        var dir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(dir);
        var baseName = $"{section.SectionId}_{Slug(section.Title)}";
        var wavPath = Path.Combine(dir, baseName + ".wav");
        var srtPath = options.Subtitles ? Path.Combine(dir, baseName + ".srt") : null;
        var vttPath = options.Subtitles ? Path.Combine(dir, baseName + ".vtt") : null;
        var manifestPath = Path.Combine(dir, baseName + ".manifest.json");
        var targets = new[] { wavPath, srtPath, vttPath, manifestPath }.Where(t => t is not null).Cast<string>().ToList();
        if (!options.Overwrite && targets.FirstOrDefault(File.Exists) is { } existing)
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"{existing} already exists. Choose another folder or allow overwriting.", new { path = existing });

        var tmpWav = wavPath + ".tmp";
        if (File.Exists(tmpWav)) File.Delete(tmpWav);
        buffer.WriteWav(tmpWav);
        var info = WavInfo.Read(tmpWav);
        File.Move(tmpWav, wavPath, overwrite: true);

        var scenes = segments.GroupBy(s => s.SceneId)
            .Select(g => new AssembledScene(g.Key, g.First().SceneTitle, g.First().StartMs, g.Last().EndMs)).ToList();
        bool speakers = store.Kind.UsesSpeakers;
        if (options.Subtitles)
        {
            FileUtil.WriteAllTextAtomic(srtPath!, Subtitles.Srt(segments, speakers, options.SubtitleLineChars));
            FileUtil.WriteAllTextAtomic(vttPath!, Subtitles.Vtt(segments, speakers, options.SubtitleLineChars));
        }

        var manifest = new AssemblyManifest(
            SchemaVersion: 1,
            Kind: "noorvox.assembly",
            Generator: $"NoorVox {NoorVoxInfo.Version}",
            CreatedAt: DateTimeOffset.Now,
            Project: new { store.Project.ProjectId, store.Project.Title, store.Project.Kind, root = store.Root },
            Section: new { section.SectionId, section.Title },
            Audio: new AssemblyAudio(wavPath, info.SampleRate, info.Channels, info.BitsPerSample, info.Samples, info.DurationMs,
                new FileInfo(wavPath).Length, FileUtil.Sha256File(wavPath)),
            Subtitles: options.Subtitles ? new { srt = srtPath, vtt = vttPath, granularity = "segment" } : null,
            Pauses: new AssemblyPauses(pauses.BetweenSegmentsMs, pauses.BetweenScenesMs, pauses.SectionEdgeMs, PauseRule),
            Processing: new AssemblyProcessing(rate, resampled, resampled ? "windowed-sinc (Blackman, 32 zero-crossings)" : "none",
                gainDb is not null, gainDb, Math.Round(20 * Math.Log10(Math.Max(buffer.Peak(), 1e-9)), 2)),
            Scenes: scenes,
            Segments: segments,
            Missing: missing,
            Stale: stale,
            Timing: "Segment times are exact sample positions of each approved take in this file (ms = samples / sampleRate). No word-level alignment.");
        FileUtil.WriteAllTextAtomic(manifestPath, Json.Serialize(manifest));
        return new AssemblyResult(manifestPath, wavPath, srtPath, vttPath, manifest);
    }

    static long Ms(long samples, int rate) => (long)Math.Round(samples * 1000.0 / rate);

    static string Slug(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        return s.Length == 0 ? "section" : s.Length > 48 ? s[..48].TrimEnd('_') : s;
    }
}

/// <summary>Segment-level captions. One cue per segment at its measured position; long text is wrapped, never re-timed.</summary>
public static class Subtitles
{
    public static string Srt(IReadOnlyList<AssembledSegment> segments, bool speakers, int lineChars)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var s in segments)
        {
            sb.Append(++n).Append("\r\n");
            sb.Append(Time(s.StartMs, ',')).Append(" --> ").Append(Time(s.EndMs, ',')).Append("\r\n");
            var text = speakers && s.Speaker is { Length: > 0 } sp ? $"{sp.ToUpperInvariant()}: {s.Text}" : s.Text;
            foreach (var line in Wrap(text, lineChars)) sb.Append(line).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Vtt(IReadOnlyList<AssembledSegment> segments, bool speakers, int lineChars)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var s in segments)
        {
            sb.Append(s.SegmentId).Append('\n');
            sb.Append(Time(s.StartMs, '.')).Append(" --> ").Append(Time(s.EndMs, '.')).Append('\n');
            var lines = Wrap(Escape(s.Text), lineChars).ToList();
            if (speakers && s.Speaker is { Length: > 0 } sp) lines[0] = $"<v {Escape(sp)}>{lines[0]}";
            foreach (var line in lines) sb.Append(line).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Time(long ms, char sep)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";
    }

    public static IEnumerable<string> Wrap(string text, int max)
    {
        var words = text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > max) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}

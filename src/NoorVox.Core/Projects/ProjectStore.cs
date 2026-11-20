using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NoorVox.Core.Models;
using NoorVox.Core.Util;

namespace NoorVox.Core.Projects;

/// <summary>What will actually be synthesized for a segment, after speaker/default fallbacks and pronunciation.</summary>
public sealed record EffectiveSegment(string SpokenText, string VoiceId, double Speed, string CacheKey, string? Style = null);

public sealed record SegmentLocation(SectionRef Section, SectionFile File, Scene Scene, Segment Segment, int IndexInProject);

/// <summary>
/// Reads and writes one project folder. All writes are atomic (temp file + replace) and serialised across processes
/// by a short-lived lock file, so the studio and the CLI can work on the same project without corrupting it.
/// </summary>
public sealed partial class ProjectStore
{
    public const string ProjectFileName = "noorvox.project.json";
    public string Root { get; }
    public ProjectFile Project { get; private set; }
    public ProjectKind Kind => ProjectKinds.Get(Project.Kind);
    string ProjectPath => Path.Combine(Root, ProjectFileName);
    public string StateDir => Path.Combine(Root, ".noorvox");
    public string JobsDir => Path.Combine(StateDir, "jobs");
    public string TempDir => Path.Combine(StateDir, "tmp");
    public string TakesDir => Path.Combine(Root, "takes");

    ProjectStore(string root, ProjectFile project)
    {
        Root = root;
        Project = project;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id) && !id.Contains("..");

    // ------------------------------------------------------------------ create / open

    public static ProjectStore Create(string directory, string title, string kind, string? voiceId = null, string? model = null)
    {
        var root = Path.GetFullPath(directory);
        var k = ProjectKinds.Get(kind);
        if (string.IsNullOrWhiteSpace(title)) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "A project needs a title.");
        if (File.Exists(Path.Combine(root, ProjectFileName)))
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"A NoorVox project already exists in {root}.", new { path = root });
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"{root} is not empty. Choose a new or empty folder for the project.", new { path = root });

        foreach (var d in new[] { root, Path.Combine(root, "scripts"), Path.Combine(root, "takes"), Path.Combine(root, "references"), Path.Combine(root, "voices"), Path.Combine(root, "exports"), Path.Combine(root, ".noorvox", "jobs") })
            Directory.CreateDirectory(d);

        var now = DateTimeOffset.Now;
        var project = new ProjectFile
        {
            ProjectId = Guid.NewGuid().ToString("N")[..12],
            Title = title.Trim(),
            Kind = k.Id,
            CreatedAt = now,
            ModifiedAt = now,
            Defaults = new ProjectDefaults { VoiceId = voiceId, Model = model, Speed = 1.0 },
            Pauses = new PauseSettings { BetweenSegmentsMs = k.BetweenSegmentsMs, BetweenScenesMs = k.BetweenScenesMs, SectionEdgeMs = k.SectionEdgeMs },
            Generator = $"NoorVox {NoorVoxInfo.Version}",
        };
        if (!k.UsesSpeakers) project.Speakers.Add(new Speaker { SpeakerId = "narrator", Name = "Narrator" });
        FileUtil.WriteAllTextAtomic(Path.Combine(root, ProjectFileName), Json.Serialize(project));
        FileUtil.WriteAllTextAtomic(Path.Combine(root, "voices", "pronunciation.json"), Json.Serialize(new PronunciationFile()));
        return new ProjectStore(root, project);
    }

    public static ProjectStore Open(string directory)
    {
        var root = Path.GetFullPath(directory);
        var path = Path.Combine(root, ProjectFileName);
        if (!File.Exists(path))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No NoorVox project in {root} (missing {ProjectFileName}).", new { path = root });
        ProjectFile project;
        try { project = Json.ReadFile<ProjectFile>(path); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"{ProjectFileName} is not valid JSON: {ex.Message}", new { path }, ex);
        }
        if (project.SchemaVersion != 1)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Project schemaVersion {project.SchemaVersion} is not supported by NoorVox {NoorVoxInfo.Version}.");
        return new ProjectStore(root, project);
    }

    public void Reload() => Project = Json.ReadFile<ProjectFile>(ProjectPath);

    public void Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "A project needs a title.");
        MutateProject(p => p.Title = title.Trim());
    }

    /// <summary>
    /// Copies the project to a new (empty) folder with a new id: scripts, takes (so approvals stay valid), references and
    /// pronunciation. Job history, locks, temp audio and exports stay with the original.
    /// </summary>
    public ProjectStore Duplicate(string destination, string? title = null)
    {
        var dest = Path.GetFullPath(destination);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"{dest} is not empty. Choose a new or empty folder.", new { path = dest });
        if (dest.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "A project can't be duplicated into itself.");
        Reload();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root, file);
            var top = rel.Split(Path.DirectorySeparatorChar)[0];
            if (top is ".noorvox" or "exports") continue;
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetAttributes(target, File.GetAttributes(file)); // keep references read-only
        }
        foreach (var d in new[] { "exports", Path.Combine(".noorvox", "jobs") }) Directory.CreateDirectory(Path.Combine(dest, d));
        var copy = Json.ReadFile<ProjectFile>(Path.Combine(dest, ProjectFileName));
        copy.ProjectId = Guid.NewGuid().ToString("N")[..12];
        copy.Title = string.IsNullOrWhiteSpace(title) ? $"{Project.Title} (copy)" : title.Trim();
        copy.CreatedAt = copy.ModifiedAt = DateTimeOffset.Now;
        FileUtil.WriteAllTextAtomic(Path.Combine(dest, ProjectFileName), Json.Serialize(copy));
        return Open(dest);
    }

    // ------------------------------------------------------------------ locking & mutation

    /// <summary>Cross-process write lock (short-lived). Readers never lock: files are replaced atomically.</summary>
    IDisposable WriteLock()
    {
        Directory.CreateDirectory(StateDir);
        var path = Path.Combine(StateDir, "write.lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
            catch (IOException ex) { throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, "The project is busy (another NoorVox process is writing). Try again.", null, ex); }
        }
    }

    public void MutateProject(Action<ProjectFile> change)
    {
        using var _ = WriteLock();
        var fresh = Json.ReadFile<ProjectFile>(ProjectPath);
        change(fresh);
        fresh.ModifiedAt = DateTimeOffset.Now;
        FileUtil.WriteAllTextAtomic(ProjectPath, Json.Serialize(fresh));
        Project = fresh;
    }

    public SectionFile LoadSection(string sectionId)
    {
        var r = Project.Sections.FirstOrDefault(s => s.SectionId == sectionId)
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No section '{sectionId}' in this project.");
        return Json.ReadFile<SectionFile>(SafePath(r.File));
    }

    public IEnumerable<SectionFile> LoadSections() => Project.Sections.Select(s => LoadSection(s.SectionId));

    /// <summary>Read-modify-write of one section file under the project lock (reads the latest copy from disk).</summary>
    public T MutateSection<T>(string sectionId, Func<SectionFile, T> change, bool touchProject = true)
    {
        using var _ = WriteLock();
        Reload();
        var r = Project.Sections.First(s => s.SectionId == sectionId);
        var path = SafePath(r.File);
        var file = Json.ReadFile<SectionFile>(path);
        var result = change(file);
        FileUtil.WriteAllTextAtomic(path, Json.Serialize(file));
        if (!touchProject && file.Title == r.Title) return result;
        var fresh = Json.ReadFile<ProjectFile>(ProjectPath);
        fresh.ModifiedAt = DateTimeOffset.Now;
        var title = fresh.Sections.First(s => s.SectionId == sectionId);
        title.Title = file.Title;
        FileUtil.WriteAllTextAtomic(ProjectPath, Json.Serialize(fresh));
        Project = fresh;
        return result;
    }

    /// <summary>Resolves a project-relative path and refuses anything that escapes the project folder.</summary>
    public string SafePath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Path '{relative}' points outside the project.");
        return full;
    }

    // ------------------------------------------------------------------ structure

    public sealed record ImportedScene(string Title, IReadOnlyList<ImportedSegment> Segments);
    public sealed record ImportedSegment(string Text, string? SegmentId = null, string? SpeakerName = null, string? Notes = null);

    /// <summary>Appends a section with generated stable ids (e.g. <c>ep02.s01.p003</c>) unless explicit ids are given (dialogue line ids).</summary>
    public SectionFile AddSection(string title, IReadOnlyList<ImportedScene> scenes, string? sectionId = null)
    {
        using var _ = WriteLock();
        Reload();
        var existingIds = AllSegmentIds();
        sectionId ??= NextSectionId();
        if (!IsValidId(sectionId) || Project.Sections.Any(s => s.SectionId == sectionId))
            sectionId = NextSectionId();

        var file = new SectionFile { SectionId = sectionId, Title = string.IsNullOrWhiteSpace(title) ? sectionId : title.Trim() };
        int sceneNo = 0;
        foreach (var sc in scenes)
        {
            var scene = new Scene { SceneId = $"{sectionId}.s{++sceneNo:00}", Title = string.IsNullOrWhiteSpace(sc.Title) ? $"{Kind.SceneLabel} {sceneNo}" : sc.Title.Trim() };
            int segNo = 0;
            foreach (var seg in sc.Segments)
            {
                segNo++;
                string id = seg.SegmentId is { } explicitId && IsValidId(explicitId) ? explicitId : $"{scene.SceneId}.p{segNo:000}";
                if (!existingIds.Add(id))
                    throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Duplicate segment id '{id}'. Segment ids must be unique in a project.");
                var speakerId = seg.SpeakerName is { Length: > 0 } name ? EnsureSpeaker(name) : Kind.UsesSpeakers ? null : "narrator";
                scene.Segments.Add(new Segment { SegmentId = id, Text = seg.Text.Trim(), SpeakerId = speakerId, Notes = seg.Notes });
            }
            file.Scenes.Add(scene);
        }

        var rel = $"scripts/{sectionId}.json";
        FileUtil.WriteAllTextAtomic(SafePath(rel), Json.Serialize(file));
        var fresh = Project;
        fresh.Sections.Add(new SectionRef { SectionId = sectionId, Title = file.Title, File = rel });
        fresh.ModifiedAt = DateTimeOffset.Now;
        FileUtil.WriteAllTextAtomic(ProjectPath, Json.Serialize(fresh));
        return file;
    }

    string NextSectionId()
    {
        int n = Project.Sections.Count + 1;
        string id;
        do id = $"{Kind.SectionPrefix}{n++:00}"; while (Project.Sections.Any(s => s.SectionId == id));
        return id;
    }

    public HashSet<string> AllSegmentIds() =>
        Project.Sections.SelectMany(s => Json.ReadFile<SectionFile>(SafePath(s.File)).Scenes.SelectMany(sc => sc.Segments).Select(g => g.SegmentId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the speaker id for a display name, creating the speaker if needed (caller holds the lock).</summary>
    string EnsureSpeaker(string name)
    {
        var existing = Project.Speakers.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.SpeakerId;
        var baseId = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (baseId.Length == 0) baseId = "speaker";
        var id = baseId;
        for (int i = 2; Project.Speakers.Any(s => s.SpeakerId == id); i++) id = $"{baseId}_{i}";
        Project.Speakers.Add(new Speaker { SpeakerId = id, Name = name.Trim() });
        return id;
    }

    /// <summary>Adds an empty segment after <paramref name="afterSegmentId"/> (or at the end of the scene).</summary>
    public Segment AddSegment(string sceneId, string? afterSegmentId, string text = "")
    {
        var sectionId = Project.Sections.FirstOrDefault(s => LoadSection(s.SectionId).Scenes.Any(x => x.SceneId == sceneId))?.SectionId
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No scene '{sceneId}'.");
        var ids = AllSegmentIds();
        return MutateSection(sectionId, f =>
        {
            var scene = f.Scenes.FirstOrDefault(s => s.SceneId == sceneId)
                ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No scene '{sceneId}'.");
            int n = scene.Segments.Count + 1;
            string id;
            do id = $"{sceneId}.p{n++:000}"; while (ids.Contains(id));
            var seg = new Segment { SegmentId = id, Text = text, SpeakerId = Kind.UsesSpeakers ? null : "narrator" };
            int at = afterSegmentId is null ? scene.Segments.Count : scene.Segments.FindIndex(s => s.SegmentId == afterSegmentId) + 1;
            scene.Segments.Insert(Math.Clamp(at, 0, scene.Segments.Count), seg);
            return seg;
        });
    }

    /// <summary>Removes a segment from the script. Its take files stay on disk (never silently deleted).</summary>
    public void RemoveSegment(string segmentId)
    {
        var loc = Find(segmentId);
        MutateSection(loc.Section.SectionId, f =>
        {
            foreach (var sc in f.Scenes) sc.Segments.RemoveAll(s => s.SegmentId == segmentId);
            return 0;
        });
    }

    /// <summary>
    /// Splits a segment at a character position. The first part keeps the id (and its approval, which becomes stale because the
    /// text changed); the second part becomes a new segment right after it, with the same speaker/voice/speed and no takes.
    /// </summary>
    public (Segment First, Segment Second) SplitSegment(string segmentId, int at)
    {
        var loc = Find(segmentId);
        var text = loc.Segment.Text;
        if (at <= 0 || at >= text.Length || text[..at].Trim().Length == 0 || text[at..].Trim().Length == 0)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Split position must leave text on both sides.");
        var ids = AllSegmentIds();
        return MutateSection(loc.Section.SectionId, f =>
        {
            var scene = f.Scenes.First(s => s.SceneId == loc.Scene.SceneId);
            int index = scene.Segments.FindIndex(s => s.SegmentId == segmentId);
            var first = scene.Segments[index];
            int n = scene.Segments.Count + 1;
            string id;
            do id = $"{scene.SceneId}.p{n++:000}"; while (ids.Contains(id));
            var second = new Segment
            {
                SegmentId = id, Text = first.Text[at..].Trim(), SpeakerId = first.SpeakerId, VoiceId = first.VoiceId, Speed = first.Speed,
                PauseAfterMs = first.PauseAfterMs,
            };
            first.Text = first.Text[..at].Trim();
            first.PauseAfterMs = null;
            scene.Segments.Insert(index + 1, second);
            return (first, second);
        });
    }

    /// <summary>
    /// Appends the next segment of the same scene to this one and removes it from the script (its take files stay on disk).
    /// The merged segment keeps its id and approval pointer (now stale, since the text changed).
    /// </summary>
    public Segment MergeWithNext(string segmentId)
    {
        var loc = Find(segmentId);
        return MutateSection(loc.Section.SectionId, f =>
        {
            var scene = f.Scenes.First(s => s.SceneId == loc.Scene.SceneId);
            int index = scene.Segments.FindIndex(s => s.SegmentId == segmentId);
            if (index + 1 >= scene.Segments.Count)
                throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"{segmentId} is the last segment of its scene; nothing to merge with.");
            var a = scene.Segments[index];
            var b = scene.Segments[index + 1];
            if (a.SpeakerId != b.SpeakerId)
                throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Can't merge segments spoken by different speakers.");
            a.Text = $"{a.Text.TrimEnd()} {b.Text.TrimStart()}".Trim();
            a.Notes = string.Join(" · ", new[] { a.Notes, b.Notes }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } notes ? notes : null;
            a.PauseAfterMs = b.PauseAfterMs;
            scene.Segments.RemoveAt(index + 1);
            return a;
        });
    }

    /// <summary>Replaces the pronunciation rules (validated). Returns how many segments' spoken text changes as a result.</summary>
    public int SavePronunciation(IReadOnlyList<PronunciationRule> rules)
    {
        if (rules.Count > 500) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "At most 500 pronunciation rules.");
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Match) || r.Match.Length > 100 || r.Replace.Length > 200)
                throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Each rule needs a word (≤100 characters) and a replacement (≤200).");
        }
        if (rules.GroupBy(r => r.CaseSensitive ? r.Match : r.Match.ToLowerInvariant()).Any(g => g.Count() > 1))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Each word can only have one rule.");
        var before = Pronunciation();
        var after = new PronunciationFile { Rules = rules.Select(r => new PronunciationRule { Match = r.Match.Trim(), Replace = r.Replace.Trim(), WholeWord = r.WholeWord, CaseSensitive = r.CaseSensitive }).ToList() };
        using (WriteLock())
            FileUtil.WriteAllTextAtomic(Path.Combine(Root, "voices", "pronunciation.json"), Json.Serialize(after));
        return AllSegments().Count(l => before.Apply(l.Segment.Text) != after.Apply(l.Segment.Text));
    }

    public Segment UpdateSegment(string segmentId, Action<Segment> change)
    {
        var loc = Find(segmentId);
        return MutateSection(loc.Section.SectionId, f =>
        {
            var seg = f.Scenes.SelectMany(s => s.Segments).First(s => s.SegmentId == segmentId);
            change(seg);
            return seg;
        });
    }

    public IEnumerable<SegmentLocation> AllSegments()
    {
        int i = 0;
        foreach (var r in Project.Sections)
        {
            var f = Json.ReadFile<SectionFile>(SafePath(r.File));
            foreach (var sc in f.Scenes)
                foreach (var seg in sc.Segments)
                    yield return new SegmentLocation(r, f, sc, seg, i++);
        }
    }

    public SegmentLocation Find(string segmentId) =>
        AllSegments().FirstOrDefault(l => l.Segment.SegmentId == segmentId)
        ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No segment '{segmentId}' in this project.");

    // ------------------------------------------------------------------ effective settings, takes, status

    public PronunciationFile Pronunciation()
    {
        var p = Path.Combine(Root, "voices", "pronunciation.json");
        return File.Exists(p) ? Json.ReadFile<PronunciationFile>(p) : new PronunciationFile();
    }

    public EffectiveSegment Effective(Segment seg, InstalledModel model, PronunciationFile? pron = null)
    {
        var speaker = seg.SpeakerId is null ? null : Project.Speakers.FirstOrDefault(s => s.SpeakerId == seg.SpeakerId);
        var voice = seg.VoiceId ?? speaker?.VoiceId ?? Project.Defaults.VoiceId ?? model.Manifest.DefaultVoice ?? "af_heart";
        var speed = seg.Speed ?? speaker?.Speed ?? Project.Defaults.Speed;
        // Style applies only where the model takes it; an empty segment style switches an inherited one off.
        var style = model.Capabilities.Style || model.Capabilities.Design ? seg.Style ?? speaker?.Style ?? Project.Defaults.Style : null;
        style = string.IsNullOrWhiteSpace(style) ? null : style.Trim();
        var spoken = (pron ?? Pronunciation()).Apply(seg.Text);
        var key = CacheKey(spoken, voice, speed, model, style);
        return new EffectiveSegment(spoken, voice, speed, key, style);
    }

    public static string CacheKey(string spokenText, string voice, double speed, InstalledModel model, string? style = null)
    {
        // Style is appended only when present, so takes made before 0.3 keep their cache keys.
        var material = $"v1\n{spokenText}\n{voice}\n{speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\n{model.ModelSha256}\n{model.Manifest.Backend}"
            + (style is null ? "" : $"\nstyle:{style}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public IReadOnlyList<TakeRecord> Takes(string segmentId)
    {
        if (!IsValidId(segmentId)) return [];
        var dir = Path.Combine(TakesDir, segmentId);
        if (!Directory.Exists(dir)) return [];
        var list = new List<TakeRecord>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var t = Json.ReadFile<TakeRecord>(f);
                if (File.Exists(SafePath(t.File))) list.Add(t); // a take is complete only when its WAV exists too
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException) { }
        }
        return list.OrderBy(t => t.CreatedAt).ToList();
    }

    public string Status(Segment seg, EffectiveSegment eff, IReadOnlyList<TakeRecord> takes)
    {
        if (string.IsNullOrWhiteSpace(seg.Text)) return SegmentStatus.Empty;
        var approved = seg.ApprovedTakeId is null ? null : takes.FirstOrDefault(t => t.TakeId == seg.ApprovedTakeId);
        if (approved is not null) return approved.CacheKey == eff.CacheKey ? SegmentStatus.Approved : SegmentStatus.Stale;
        return takes.Any(t => t.CacheKey == eff.CacheKey) ? SegmentStatus.Generated : SegmentStatus.Missing;
    }

    /// <summary>Sets (or clears) the approved take. The take files themselves are never modified.</summary>
    public void Approve(string segmentId, string? takeId)
    {
        if (takeId is not null && Takes(segmentId).All(t => t.TakeId != takeId))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Take '{takeId}' does not exist for segment '{segmentId}'.");
        UpdateSegment(segmentId, s => s.ApprovedTakeId = takeId);
    }

    /// <summary>Stores the original import untouched in <c>references/</c> (read-only reference, spec §6.1).</summary>
    public string KeepReference(string sourceFile)
    {
        var dir = Path.Combine(Root, "references");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(dir, $"{stamp}_{Path.GetFileName(sourceFile)}");
        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{stamp}_{i}_{Path.GetFileName(sourceFile)}");
        File.Copy(sourceFile, dest);
        File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.ReadOnly);
        return dest;
    }
}

/// <summary>
/// <c>voices/pronunciation.json</c>: plain text replacements applied before synthesis (e.g. "NoorEngine" → "Noor Engine").
/// Phonetic notation is not offered: Kokoro's custom phoneme support isn't exposed by NoorVox yet, so nothing pretends it is.
/// </summary>
public sealed class PronunciationFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<PronunciationRule> Rules { get; set; } = [];

    public string Apply(string text)
    {
        var result = text;
        foreach (var r in Rules.Where(r => !string.IsNullOrEmpty(r.Match)))
        {
            var pattern = r.WholeWord ? $@"\b{Regex.Escape(r.Match)}\b" : Regex.Escape(r.Match);
            result = Regex.Replace(result, pattern, r.Replace.Replace("$", "$$"), r.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        }
        return result;
    }
}

public sealed class PronunciationRule
{
    public string Match { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool WholeWord { get; set; } = true;
    public bool CaseSensitive { get; set; }
}

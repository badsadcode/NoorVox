namespace NoorVox.Core.Projects;

/// <summary>
/// A project kind is a workflow template (labels, id prefixes, default pauses), not a locked format (spec §2):
/// every project has the same Section → Scene → Segment structure.
/// </summary>
public sealed record ProjectKind(
    string Id, string DisplayName, string Description,
    string SectionLabel, string SceneLabel, string SegmentLabel, string SectionPrefix,
    int BetweenSegmentsMs, int BetweenScenesMs, int SectionEdgeMs, bool UsesSpeakers);

public static class ProjectKinds
{
    public static readonly IReadOnlyList<ProjectKind> All =
    [
        new("documentary", "Documentary", "Episodes and scenes of narration, e.g. a devlog documentary.", "Episode", "Scene", "Segment", "ep", 350, 900, 1500, false),
        new("audiobook", "Audiobook", "A manuscript split into chapters and paragraphs.", "Chapter", "Section", "Paragraph", "ch", 450, 1200, 2000, false),
        new("dialogue", "Game dialogue", "Character lines with stable line IDs and per-character voices.", "Dialogue set", "Conversation", "Line", "set", 250, 600, 0, true),
        new("narration", "Narration", "General narration split into parts.", "Part", "Section", "Segment", "part", 350, 900, 1200, false),
        new("education", "Education", "Lessons and explainers.", "Lesson", "Section", "Segment", "lesson", 400, 900, 1500, false),
        new("custom", "Custom", "Your own structure.", "Section", "Group", "Segment", "sec", 350, 900, 1200, true),
    ];

    public static ProjectKind Get(string id) =>
        All.FirstOrDefault(k => k.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
            $"Unknown project kind '{id}'. Use one of: {string.Join(", ", All.Select(k => k.Id))}.");
}

/// <summary><c>noorvox.project.json</c> — the project root. Documented in docs/PROJECT_FORMAT.md.</summary>
public sealed class ProjectFile
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "narration";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public ProjectDefaults Defaults { get; set; } = new();
    public PauseSettings Pauses { get; set; } = new();
    public List<Speaker> Speakers { get; set; } = [];
    public List<SectionRef> Sections { get; set; } = [];
    public string Generator { get; set; } = "";
}

public sealed class ProjectDefaults
{
    /// <summary>Model reference (<c>kokoro-82m</c> or <c>kokoro-82m@1.0</c>); null = NoorVox default.</summary>
    public string? Model { get; set; }
    public string? VoiceId { get; set; }
    public double Speed { get; set; } = 1.0;
}

/// <summary>Structured pauses in milliseconds (spec §6.2) — used by export assembly (Phase 4), stored now.</summary>
public sealed class PauseSettings
{
    public int BetweenSegmentsMs { get; set; }
    public int BetweenScenesMs { get; set; }
    public int SectionEdgeMs { get; set; }
}

/// <summary>A character / narrator. Segments reference it by id; its voice can be recast in one place.</summary>
public sealed class Speaker
{
    public string SpeakerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? VoiceId { get; set; }
    public double? Speed { get; set; }
}

public sealed class SectionRef
{
    public string SectionId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Relative path, e.g. <c>scripts/ep01.json</c>.</summary>
    public string File { get; set; } = "";
}

/// <summary><c>scripts/&lt;sectionId&gt;.json</c> — one episode / chapter / dialogue set.</summary>
public sealed class SectionFile
{
    public int SchemaVersion { get; set; } = 1;
    public string SectionId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<Scene> Scenes { get; set; } = [];
}

public sealed class Scene
{
    public string SceneId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<Segment> Segments { get; set; } = [];
}

/// <summary>The atomic unit of generation, caching, retry and approval. <see cref="SegmentId"/> never changes.</summary>
public sealed class Segment
{
    public string SegmentId { get; set; } = "";
    public string Text { get; set; } = "";
    public string? SpeakerId { get; set; }
    public string? VoiceId { get; set; }
    public double? Speed { get; set; }
    public int? PauseBeforeMs { get; set; }
    public int? PauseAfterMs { get; set; }
    /// <summary>Editorial notes — never spoken.</summary>
    public string? Notes { get; set; }
    /// <summary>Pointer to the approved take. Approved take files are immutable; approval only moves this pointer.</summary>
    public string? ApprovedTakeId { get; set; }
}

/// <summary><c>takes/&lt;segmentId&gt;/&lt;takeId&gt;.json</c> — written after its WAV, so its presence marks a complete take.</summary>
public sealed class TakeRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string TakeId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    /// <summary>sha256 over everything that affects the audio (spoken text, voice, speed, model hash, backend).</summary>
    public string CacheKey { get; set; } = "";
    /// <summary>Exactly what was sent to the engine (after pronunciation replacements).</summary>
    public string SpokenText { get; set; } = "";
    public string SourceTextSha256 { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public double Speed { get; set; }
    public string ModelId { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string ModelSha256 { get; set; } = "";
    public string Backend { get; set; } = "";
    /// <summary>Relative to the project root, e.g. <c>takes/ep01.s01.p001/t20261120-1530-ab12.wav</c>.</summary>
    public string File { get; set; } = "";
    public long DurationMs { get; set; }
    public int SampleRate { get; set; }
    public long Samples { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? JobId { get; set; }
    public string Generator { get; set; } = "";
}

/// <summary>Where a segment stands. Computed, never stored.</summary>
public static class SegmentStatus
{
    /// <summary>No text to speak.</summary>
    public const string Empty = "empty";
    /// <summary>No take matches the current text/voice/speed/model.</summary>
    public const string Missing = "missing";
    /// <summary>A take matches the current settings but none is approved.</summary>
    public const string Generated = "generated";
    /// <summary>The approved take matches the current settings.</summary>
    public const string Approved = "approved";
    /// <summary>A take is approved but the text/voice/speed/model changed since; it stays approved until you choose.</summary>
    public const string Stale = "stale";
}

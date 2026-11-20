using System.Text.RegularExpressions;
using NoorVox.Core.Audio;
using NoorVox.Core.Util;

namespace NoorVox.Core.Backends;

/// <summary>What the person creating a cloned voice confirmed, stored verbatim with the profile.</summary>
public sealed record ConsentRecord(string Basis, string Statement, DateTimeOffset ConfirmedAt);

/// <summary>
/// A cloned voice: a copy of a reference recording plus its transcript, for models that can clone (Qwen3-TTS Base).
/// Lives in <c>&lt;models&gt;/voice-profiles/&lt;voiceId&gt;/profile.json</c> next to <c>reference.wav</c>. Profiles are
/// immutable: a different reference makes a different profile. profile.json is written last (commit marker).
/// </summary>
public sealed class VoiceProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string VoiceId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary><c>clone</c> (a real person's recording, with consent) · <c>designed</c> (made by Qwen VoiceDesign from a
    /// description) · <c>synthetic</c> (saved from a NoorVox take of a built-in or pack voice). Only <c>clone</c> involves a real voice.</summary>
    public string Kind { get; set; } = "clone";
    /// <summary>designed: the written description the voice was made from.</summary>
    public string? Description { get; set; }
    /// <summary>designed/synthetic: model reference and voice that generated the reference audio.</summary>
    public string? SourceModel { get; set; }
    public string? SourceVoice { get; set; }
    /// <summary>clone from the Voice lab: the recording it came from.</summary>
    public string? SourceRecordingId { get; set; }
    /// <summary>Language of the reference recording (<c>auto</c> if unknown).</summary>
    public string Language { get; set; } = "auto";
    public string Gender { get; set; } = "unknown";
    public string ReferenceFile { get; set; } = "reference.wav";
    public string ReferenceSha256 { get; set; } = "";
    public long ReferenceDurationMs { get; set; }
    /// <summary>Exactly what is said in the reference recording. Qwen uses it to align the voice.</summary>
    public string Transcript { get; set; } = "";
    public ConsentRecord Consent { get; set; } = new("", "", default);
    /// <summary>File name only (never the full original path) of the recording the user picked.</summary>
    public string SourceFileName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Generator { get; set; } = "";

    /// <summary>Set when read from disk; not stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Directory { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string ReferencePath => Path.Combine(Directory, ReferenceFile);
}

public sealed record CreateProfileRequest(string Name, string ReferenceWav, string Transcript, string ConsentBasis, string Language = "auto", string Gender = "unknown",
    string? SourceRecordingId = null);

/// <summary>A profile whose reference audio NoorVox generated itself (no real person's voice), so no consent statement is asked for.</summary>
public sealed record GeneratedProfileRequest(string Name, string ReferenceWav, string Transcript, string Kind, string SourceModel, string SourceVoice,
    string? Description, string Language = "auto", string Gender = "unknown");

public static partial class VoiceProfiles
{
    public const string FolderName = "voice-profiles";
    public const double MinReferenceSeconds = 2.0, MaxReferenceSeconds = 60.0;

    /// <summary>The statements a user must confirm. The chosen one is stored verbatim.</summary>
    public static readonly IReadOnlyDictionary<string, string> ConsentStatements = new Dictionary<string, string>
    {
        ["own-voice"] = "This recording is my own voice.",
        ["speaker-permission"] = "I have the speaker's permission to create and use a synthetic copy of their voice.",
    };

    /// <summary>Recorded for generated profiles instead of a person's consent (they contain no real person's voice).</summary>
    public static readonly IReadOnlyDictionary<string, string> GeneratedStatements = new Dictionary<string, string>
    {
        ["designed"] = "Synthetic voice designed by Qwen3-TTS VoiceDesign from a written description; no real person's recording was used.",
        ["synthetic"] = "Synthetic voice saved from a NoorVox take of a built-in voice; no real person's recording was used.",
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex IdPattern();
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static string Root(string modelsDir) => Path.Combine(modelsDir, FolderName);

    public static IReadOnlyList<VoiceProfile> ReadAll(string modelsDir)
    {
        var root = Root(modelsDir);
        if (!System.IO.Directory.Exists(root)) return [];
        var list = new List<VoiceProfile>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(dir, "profile.json");
            if (!File.Exists(file)) continue; // incomplete
            try
            {
                var p = Json.ReadFile<VoiceProfile>(file);
                if (!IsValidId(p.VoiceId) || Path.GetFileName(dir) != p.VoiceId) continue;
                p.Directory = dir;
                if (File.Exists(p.ReferencePath)) list.Add(p);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException) { }
        }
        return list.OrderBy(p => p.VoiceId, StringComparer.Ordinal).ToList();
    }

    public static VoiceProfile? Find(string modelsDir, string voiceId) => ReadAll(modelsDir).FirstOrDefault(p => p.VoiceId == voiceId);

    public static string Slug(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s.Length == 0 || !char.IsAsciiLetterLower(s[0])) s = "voice_" + s;
        return s.Length > 40 ? s[..40].TrimEnd('_') : s;
    }

    /// <summary>Validates, copies the reference recording into the profile folder and records consent. Never overwrites.</summary>
    public static VoiceProfile Create(string modelsDir, CreateProfileRequest req)
    {
        if (!ConsentStatements.TryGetValue(req.ConsentBasis, out var statement))
            throw Invalid($"Voice cloning needs consent: use {string.Join(" or ", ConsentStatements.Keys)}.");
        return Write(modelsDir, req.Name, req.ReferenceWav, req.Transcript, req.Language, req.Gender, "clone",
            new ConsentRecord(req.ConsentBasis, statement, DateTimeOffset.Now), p => p.SourceRecordingId = req.SourceRecordingId);
    }

    /// <summary>A designed or synthetic voice: the reference is audio NoorVox generated, so its provenance replaces a consent statement.</summary>
    public static VoiceProfile CreateGenerated(string modelsDir, GeneratedProfileRequest req)
    {
        if (!GeneratedStatements.TryGetValue(req.Kind, out var statement)) throw Invalid($"Unknown generated profile kind '{req.Kind}'.");
        return Write(modelsDir, req.Name, req.ReferenceWav, req.Transcript, req.Language, req.Gender, req.Kind,
            new ConsentRecord(req.Kind, statement, DateTimeOffset.Now), p =>
            {
                p.Description = req.Description;
                p.SourceModel = req.SourceModel;
                p.SourceVoice = req.SourceVoice;
            });
    }

    static VoiceProfile Write(string modelsDir, string rawName, string referenceWav, string rawTranscript, string language, string gender, string kind,
        ConsentRecord consent, Action<VoiceProfile> extra)
    {
        var name = rawName.Trim();
        if (name.Length is 0 or > 60)
            throw Invalid("Give the voice a name (1-60 characters).");
        var transcript = rawTranscript.Trim();
        if (transcript.Length < 2)
            throw Invalid("The transcript of the reference recording is required (what is said in it, word for word).");
        var source = Path.GetFullPath(referenceWav);
        if (!File.Exists(source)) throw Invalid($"Reference recording not found: {source}");
        WavInfo info;
        try { info = WavInfo.Read(source); }
        catch (InvalidDataException ex) { throw Invalid($"The reference must be a PCM WAV file: {ex.Message}"); }
        if (info.DurationSeconds < MinReferenceSeconds || info.DurationSeconds > MaxReferenceSeconds)
            throw Invalid($"The reference is {info.DurationSeconds:0.0} s; use {MinReferenceSeconds:0}-{MaxReferenceSeconds:0} s of clean speech (5-15 s works best).");

        var id = Slug(name);
        var dir = Path.Combine(Root(modelsDir), id);
        if (System.IO.Directory.Exists(dir))
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError,
                $"A voice profile '{id}' already exists. Profiles are immutable; choose another name.", new { voiceId = id });

        var staging = dir + ".creating";
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        System.IO.Directory.CreateDirectory(staging);
        var refCopy = Path.Combine(staging, "reference.wav");
        File.Copy(source, refCopy);
        var profile = new VoiceProfile
        {
            VoiceId = id, Name = name, Kind = kind, Language = language.Trim().ToLowerInvariant(), Gender = gender,
            ReferenceSha256 = FileUtil.Sha256File(refCopy), ReferenceDurationMs = info.DurationMs, Transcript = transcript,
            Consent = consent, SourceFileName = Path.GetFileName(source), CreatedAt = DateTimeOffset.Now, Generator = $"NoorVox {NoorVoxInfo.Version}",
        };
        extra(profile);
        FileUtil.WriteAllTextAtomic(Path.Combine(staging, "profile.json"), Json.Serialize(profile));
        System.IO.Directory.Move(staging, dir);
        profile.Directory = dir;
        return profile;
    }

    /// <summary>Deletes a profile folder (its copy of the reference and its metadata). Only on explicit request.</summary>
    public static void Remove(string modelsDir, string voiceId)
    {
        if (!IsValidId(voiceId)) throw Invalid($"'{voiceId}' is not a voice profile id.");
        var dir = Path.Combine(Root(modelsDir), voiceId);
        if (!File.Exists(Path.Combine(dir, "profile.json")))
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"No voice profile '{voiceId}'.");
        System.IO.Directory.Delete(dir, recursive: true);
    }

    static NoorVoxException Invalid(string message) => new(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, message);
}

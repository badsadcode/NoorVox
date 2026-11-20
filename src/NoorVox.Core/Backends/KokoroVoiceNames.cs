using System.Globalization;
using System.Text.RegularExpressions;
using NoorVox.Core.Models;

namespace NoorVox.Core.Backends;

/// <summary>
/// Kokoro voice-id conventions (metadata only; no inference dependency): <c>af_heart</c> =
/// language <c>a</c> (American English) + gender <c>f</c> + name. Lets the CLI list voices without starting a worker.
/// </summary>
public static partial class KokoroVoiceNames
{
    [GeneratedRegex("^[a-z][fm]_[a-z0-9]+$")]
    private static partial Regex VoiceIdPattern();

    public static bool IsValidId(string id) => VoiceIdPattern().IsMatch(id);

    static readonly Dictionary<char, (string Code, string Name)> Languages = new()
    {
        ['a'] = ("en-us", "American English"),
        ['b'] = ("en-gb", "British English"),
        ['j'] = ("ja", "Japanese"),
        ['z'] = ("zh", "Mandarin Chinese"),
        ['e'] = ("es", "Spanish"),
        ['f'] = ("fr", "French"),
        ['h'] = ("hi", "Hindi"),
        ['i'] = ("it", "Italian"),
        ['p'] = ("pt-br", "Brazilian Portuguese"),
    };

    public static VoiceInfo Describe(string id, string packId)
    {
        var (code, name) = id.Length > 0 && Languages.TryGetValue(id[0], out var l) ? l : ("unknown", "Unknown");
        var gender = id.Length > 1 ? id[1] switch { 'f' => "female", 'm' => "male", _ => "unknown" } : "unknown";
        var raw = id.Contains('_') ? id[(id.IndexOf('_') + 1)..] : id;
        var display = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw);
        return new VoiceInfo(id, code, name, gender, display, packId);
    }

}

/// <summary>Describes every voice of an installed model, whatever its source (pack, blend, built-in speaker, clone).</summary>
public static class ModelVoices
{
    public static readonly IReadOnlyDictionary<string, string> LanguageNames = new Dictionary<string, string>
    {
        ["auto"] = "Any (detected)", ["english"] = "English", ["chinese"] = "Chinese", ["japanese"] = "Japanese", ["korean"] = "Korean",
        ["german"] = "German", ["french"] = "French", ["russian"] = "Russian", ["portuguese"] = "Portuguese", ["spanish"] = "Spanish",
        ["italian"] = "Italian",
    };

    public static IReadOnlyList<VoiceInfo> List(InstalledModel model) =>
        model.Voices.Select(v => Describe(model, v.VoiceId, v.PackId)).ToList();

    public static VoiceInfo Describe(InstalledModel model, string voiceId, string packId)
    {
        if (packId == InstalledModel.BuiltInPackId && model.Manifest.BuiltInVoices?.FirstOrDefault(b => b.Id == voiceId) is { } b)
            return new VoiceInfo(b.Id, b.Language, LanguageNames.GetValueOrDefault(b.Language, b.Language), b.Gender, b.DisplayName, packId,
                "built-in", b.Description);
        if (packId == InstalledModel.ProfilePackId && model.Profiles?.FirstOrDefault(p => p.VoiceId == voiceId) is { } p)
            return new VoiceInfo(p.VoiceId, p.Language, LanguageNames.GetValueOrDefault(p.Language, p.Language), p.Gender, p.Name, packId,
                "clone", $"Cloned from {p.SourceFileName} ({p.ReferenceDurationMs / 1000.0:0.#} s). Consent: {p.Consent.Statement}");
        var info = KokoroVoiceNames.Describe(voiceId, packId);
        // Blended voices show the name the user gave them.
        return packId == VoiceBlender.CustomPackId && VoiceBlender.Recipe(model, voiceId) is { } r
            ? info with { DisplayName = r.Name, Kind = "blend" } : info;
    }
}

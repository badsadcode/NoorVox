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

    public static IReadOnlyList<VoiceInfo> List(InstalledModel model) =>
        model.Voices.Select(v =>
        {
            var info = Describe(v.VoiceId, v.PackId);
            // Blended voices show the name the user gave them.
            return v.PackId == VoiceBlender.CustomPackId && VoiceBlender.Recipe(model, v.VoiceId) is { } r ? info with { DisplayName = r.Name } : info;
        }).ToList();
}

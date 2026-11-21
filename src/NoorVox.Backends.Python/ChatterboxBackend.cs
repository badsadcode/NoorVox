using System.Text.Json.Nodes;
using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;

namespace NoorVox.Backends.Python;

/// <summary>
/// Chatterbox (Resemble AI, MIT) via the chatterbox engine pack: English, Multilingual (23 languages) and Turbo. Voices are
/// the model's built-in voice (<c>default</c>) or any voice profile (clone / designed / saved) — Chatterbox needs only the
/// reference audio, no transcript. Controls: emotion (exaggeration) and pace (cfg weight), except Turbo, whose code ignores
/// both. Output is always watermarked (Perth). Sidecar: <c>engines/chatterbox/noorvox_chatterbox.py</c>.
/// </summary>
public sealed class ChatterboxBackend(Action<string> log) : PythonSidecarBackend(log)
{
    public const string DefaultVoice = "default";

    /// <summary>Multilingual language names (NoorVox's vocabulary) → Chatterbox language ids.</summary>
    public static readonly IReadOnlyDictionary<string, string> LanguageIds = new Dictionary<string, string>
    {
        ["arabic"] = "ar", ["danish"] = "da", ["german"] = "de", ["greek"] = "el", ["english"] = "en", ["spanish"] = "es",
        ["finnish"] = "fi", ["french"] = "fr", ["hebrew"] = "he", ["hindi"] = "hi", ["italian"] = "it", ["japanese"] = "ja",
        ["korean"] = "ko", ["malay"] = "ms", ["dutch"] = "nl", ["norwegian"] = "no", ["polish"] = "pl", ["portuguese"] = "pt",
        ["russian"] = "ru", ["swedish"] = "sv", ["swahili"] = "sw", ["turkish"] = "tr", ["chinese"] = "zh",
    };

    public override string BackendId => "chatterbox";
    protected override string EngineName => "Chatterbox";
    protected override string EnginePackId => "chatterbox";

    /// <summary>Which Chatterbox class loads this model (english / multilingual / turbo), from the model id.</summary>
    public static string Variant(InstalledModel model) =>
        model.Id.EndsWith("-turbo", StringComparison.Ordinal) ? "turbo"
        : model.Id.EndsWith("-multilingual", StringComparison.Ordinal) ? "multilingual"
        : "english";

    protected override void AddLoadFields(InstalledModel model, JsonObject load) => load["variant"] = Variant(model);

    protected override JsonObject BuildSynthRequest(InstalledModel model, SynthesisRequest request, long seed)
    {
        var caps = model.Capabilities;
        if (Math.Abs(request.Speed - 1.0) > 1e-9) throw Unsupported("Chatterbox has no speed multiplier; use the pace control.");
        if (!string.IsNullOrWhiteSpace(request.Style)) throw Unsupported("Chatterbox takes no written style directions; use the emotion and pace controls.");
        if (request.Emotion is not null && !caps.Emotion) throw Unsupported($"{model.Manifest.DisplayName} has no emotion control.");
        if (request.Pace is not null && !caps.Pace) throw Unsupported($"{model.Manifest.DisplayName} has no pace control.");

        var msg = new JsonObject();
        string? profileLanguage = null;
        if (request.VoiceId != DefaultVoice)
        {
            var profile = FindProfile(request.VoiceId);
            msg["refAudio"] = profile.ReferencePath;
            profileLanguage = profile.Language;
        }
        if (caps.Emotion) msg["emotion"] = request.Emotion ?? caps.EmotionDefault;
        if (caps.Pace) msg["pace"] = request.Pace ?? caps.PaceDefault;

        if (Variant(model) == "multilingual")
        {
            var fallback = profileLanguage is { } pl && LanguageIds.ContainsKey(pl) ? pl : "english";
            var language = CheckLanguage(model, request.Language, fallback);
            msg["languageId"] = LanguageIds[language];
        }
        else if (request.Language is not null && request.Language != "english")
            throw Unsupported($"{model.Manifest.DisplayName} speaks English only; use Chatterbox Multilingual for other languages.");
        return msg;
    }
}

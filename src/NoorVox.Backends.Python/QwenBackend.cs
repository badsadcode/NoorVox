using System.Text.Json.Nodes;
using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;

namespace NoorVox.Backends.Python;

/// <summary>
/// Qwen3-TTS via the qwen-tts engine pack (ADR-0002): CustomVoice (built-in speakers + style), Base (cloning),
/// VoiceDesign (a voice from a description). Sidecar: <c>engines/qwen/noorvox_qwen.py</c>.
/// </summary>
public sealed class QwenBackend(Action<string> log) : PythonSidecarBackend(log)
{
    public override string BackendId => "qwen3-tts";
    protected override string EngineName => "Qwen3-TTS";
    protected override string EnginePackId => "qwen-tts";

    protected override JsonObject BuildSynthRequest(InstalledModel model, SynthesisRequest request, long seed)
    {
        var caps = model.Capabilities;
        if (Math.Abs(request.Speed - 1.0) > 1e-9) throw Unsupported("Qwen3-TTS has no speed control; ask for a pace in the style instead.");
        if (request.Emotion is not null || request.Pace is not null) throw Unsupported("Qwen3-TTS has no emotion or pace sliders; describe the delivery in the style.");
        if (!string.IsNullOrWhiteSpace(request.Style) && !caps.Style && !caps.Design)
            throw Unsupported($"{model.Manifest.DisplayName} does not take style directions.");
        if (caps.Design && string.IsNullOrWhiteSpace(request.Style))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "VoiceDesign needs a description of the voice.");

        var msg = new JsonObject();
        string language;
        if (caps.Design)
        {
            msg["mode"] = "design";
            msg["instruct"] = request.Style!.Trim(); // the description *is* the voice
            language = CheckLanguage(model, request.Language);
        }
        else if (caps.Cloning)
        {
            var profile = FindProfile(request.VoiceId);
            msg["mode"] = "clone";
            msg["refAudio"] = profile.ReferencePath;
            msg["refText"] = profile.Transcript;
            language = CheckLanguage(model, request.Language, profile.Language);
        }
        else
        {
            if (model.Manifest.BuiltInVoices?.Any(v => v.Id == request.VoiceId) != true)
                throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"'{request.VoiceId}' is not a speaker of {model.Reference}.");
            msg["mode"] = "custom";
            msg["speaker"] = request.VoiceId;
            if (!string.IsNullOrWhiteSpace(request.Style)) msg["instruct"] = request.Style.Trim();
            language = CheckLanguage(model, request.Language);
        }
        msg["language"] = language == "auto" ? "Auto" : char.ToUpperInvariant(language[0]) + language[1..];
        return msg;
    }
}

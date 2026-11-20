using System.Diagnostics;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.ML.OnnxRuntime;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;

namespace NoorVox.Backends.Kokoro;

/// <summary>Kokoro-82M via KokoroSharp + ONNX Runtime (CPU). See ADR-0001.</summary>
public sealed class KokoroBackend : ITtsBackend
{
    public const int SampleRate = 24_000;
    KokoroWavSynthesizer? _synth;
    InstalledModel? _model;
    readonly Dictionary<string, KokoroVoice> _voices = new(StringComparer.Ordinal);

    public string BackendId => "kokoro-onnx";

    public string BackendVersion { get; } =
        $"KokoroSharp {typeof(KokoroWavSynthesizer).Assembly.GetName().Version?.ToString(3)}; ONNX Runtime {OrtEnv.Instance().GetVersionString()}";

    public TtsCapabilities Capabilities { get; } = new(
        Speed: true, MinSpeed: 0.5, MaxSpeed: 2.0,
        VoiceMixing: true, Pitch: false, Style: false, Emotion: false,
        PhonemeTimestamps: true, Deterministic: false,
        ExecutionProviders: ["CPU"]);

    public string ExecutionProvider => "CPU";
    public bool IsLoaded => _synth is not null;

    public Task LoadAsync(InstalledModel model, CancellationToken ct) => Task.Run(() =>
    {
        if (_synth is not null) throw new InvalidOperationException("A model is already loaded; recycle the worker to change models.");
        _synth = new KokoroWavSynthesizer(model.ModelFile, new SessionOptions());
        _model = model;
    }, ct);

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, string outputWavPath, CancellationToken ct)
    {
        if (_synth is null || _model is null) throw new InvalidOperationException("No model loaded.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is empty.");
        if (request.Speed < Capabilities.MinSpeed || request.Speed > Capabilities.MaxSpeed)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Speed {request.Speed} is outside Kokoro's supported range {Capabilities.MinSpeed}-{Capabilities.MaxSpeed}.");

        var voice = GetVoice(request.VoiceId);
        var sw = Stopwatch.StartNew();
        // KokoroSharp's synthesis is not cancellable mid-job; cancellation = recycling the worker (ADR-0001).
        byte[] pcm = await _synth.SynthesizeAsync(request.Text, voice, new KokoroTTSPipelineConfig { Speed = (float)request.Speed });
        sw.Stop();
        if (pcm.Length == 0)
            throw new NoorVoxException(ErrorCodes.SynthesisFailed, ExitCode.SynthesisFailed,
                "Kokoro produced no audio (the text may contain nothing pronounceable in this voice's language).");

        WavWriter.WritePcm16(outputWavPath, pcm, SampleRate);
        var info = WavInfo.Read(outputWavPath);
        return new SynthesisResult(outputWavPath, info.SampleRate, info.Channels, info.BitsPerSample, info.Samples, info.DurationMs, sw.ElapsedMilliseconds);
    }

    KokoroVoice GetVoice(string voiceId)
    {
        if (_voices.TryGetValue(voiceId, out var cached)) return cached;
        // Only voices declared by an installed voice pack are loadable; the id regex also blocks path tricks.
        var entry = _model!.Voices.FirstOrDefault(v => v.VoiceId == voiceId);
        if (entry.VoiceId is null && KokoroVoiceNames.IsValidId(voiceId))
        {
            // A voice blended after this worker loaded the model: refresh the package's voice list from disk.
            _model = ModelRegistry.ReadPackage(_model.Directory);
            entry = _model.Voices.FirstOrDefault(v => v.VoiceId == voiceId);
        }
        if (!KokoroVoiceNames.IsValidId(voiceId) || entry.VoiceId is null)
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing,
                $"Voice '{voiceId}' is not installed for {_model.Reference}.", new { voiceId, model = _model.Reference });
        var voice = KokoroVoice.FromPath(Path.Combine(_model.Directory, entry.File.Path));
        _voices[voiceId] = voice;
        return voice;
    }

    public void Dispose()
    {
        _synth?.Dispose();
        _synth = null;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
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

/// <summary>
/// Kokoro-82M via KokoroSharp + ONNX Runtime. CPU by default (ADR-0001); CUDA when the settings ask for it and the
/// onnxruntime-cuda + qwen-tts engine packs are installed (ADR-0002), falling back to CPU with a note otherwise.
/// </summary>
public sealed class KokoroBackend(Action<string>? log = null) : ITtsBackend, IBackendResources
{
    public const int SampleRate = 24_000;
    const double MinSpeed = 0.5, MaxSpeed = 2.0;
    static string? _ortOverrideDir;
    KokoroWavSynthesizer? _synth;
    InstalledModel? _model;
    readonly Dictionary<string, KokoroVoice> _voices = new(StringComparer.Ordinal);
    string? _version;

    public string BackendId => "kokoro-onnx";

    public string BackendVersion => _version ??= _synth is null ? "KokoroSharp (not loaded)"
        : $"KokoroSharp {typeof(KokoroWavSynthesizer).Assembly.GetName().Version?.ToString(3)}; ONNX Runtime {OrtEnv.Instance().GetVersionString()}";

    public string ExecutionProvider { get; private set; } = "";
    public bool IsLoaded => _synth is not null;
    public GpuMemoryInfo? Gpu => null;
    public SidecarInfo? Sidecar => null;
    public string? ProviderNote { get; private set; }
    public string? LoadStage => null; // loads in seconds

    public Task LoadAsync(InstalledModel model, BackendLoadOptions options, CancellationToken ct) => Task.Run(() =>
    {
        if (_synth is not null) throw new InvalidOperationException("A model is already loaded; recycle the worker to change models.");
        bool wantCuda = options.ExecutionProvider == "cuda";
        if (wantCuda && (options.OrtCudaDirectory is null || options.CudaLibraryDirectories is not { Count: > 0 }))
        {
            wantCuda = false;
            ProviderNote = "CUDA requested, but the onnxruntime-cuda and qwen-tts engine packs are not both installed; using CPU.";
        }
        if (wantCuda) UseCudaRuntime(options);

        var so = new SessionOptions();
        ExecutionProvider = "CPU";
        if (wantCuda)
        {
            try
            {
                so.AppendExecutionProvider_CUDA(0);
                ExecutionProvider = "CUDA";
            }
            catch (Exception ex)
            {
                so.Dispose();
                so = new SessionOptions();
                ProviderNote = $"CUDA requested but unavailable ({ex.Message}); using CPU.";
            }
        }
        if (ProviderNote is not null) log?.Invoke(ProviderNote);
        _synth = new KokoroWavSynthesizer(model.ModelFile, so);
        _model = model;
        log?.Invoke($"kokoro execution provider: {ExecutionProvider}");
    }, ct);

    /// <summary>
    /// Loads ONNX Runtime's GPU build from the onnxruntime-cuda engine pack instead of the CPU build next to the worker,
    /// and lets it find CUDA/cuDNN in the qwen-tts engine's PyTorch folder. Must run before any ONNX Runtime call.
    /// </summary>
    static void UseCudaRuntime(BackendLoadOptions options)
    {
        if (_ortOverrideDir is not null) return;
        _ortOverrideDir = options.OrtCudaDirectory!;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", string.Join(';', options.CudaLibraryDirectories!.Prepend(_ortOverrideDir)) + ";" + path);
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, asm, search) =>
            name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase)
                && NativeLibrary.TryLoad(Path.Combine(_ortOverrideDir, "onnxruntime.dll"), out var h) ? h : IntPtr.Zero);
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, string outputWavPath, CancellationToken ct)
    {
        if (_synth is null || _model is null) throw new InvalidOperationException("No model loaded.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is empty.");
        if (request.Speed < MinSpeed || request.Speed > MaxSpeed)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Speed {request.Speed} is outside Kokoro's supported range {MinSpeed}-{MaxSpeed}.");
        if (!string.IsNullOrWhiteSpace(request.Style))
            throw new NoorVoxException(ErrorCodes.Unsupported, ExitCode.InvalidArguments, "Kokoro does not take style directions.");
        if (request.Emotion is not null || request.Pace is not null)
            throw new NoorVoxException(ErrorCodes.Unsupported, ExitCode.InvalidArguments, "Kokoro has no emotion or pace control; use speed.");

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
        if (entry.File is null)
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{voiceId}' is not a Kokoro voice file.");
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

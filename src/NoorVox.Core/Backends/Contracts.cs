using NoorVox.Core.Models;

namespace NoorVox.Core.Backends;

/// <summary>
/// What a backend can actually do. The UI/CLI expose only controls that are <c>true</c> here (spec principle 8).
/// </summary>
public sealed record TtsCapabilities(
    bool Speed, double MinSpeed, double MaxSpeed,
    bool VoiceMixing, bool Pitch, bool Style, bool Emotion,
    bool PhonemeTimestamps, bool Deterministic,
    IReadOnlyList<string> ExecutionProviders);

public sealed record VoiceInfo(string Id, string Language, string LanguageName, string Gender, string DisplayName, string PackId);

public sealed record SynthesisRequest(string Text, string VoiceId, double Speed);

/// <summary>Facts about a synthesized file, measured from the written audio.</summary>
public sealed record SynthesisResult(string OutputPath, int SampleRate, int Channels, int BitsPerSample, long Samples, long DurationMs, long ElapsedMs);

/// <summary>
/// One inference implementation, hosted inside an isolated worker process (never in the GUI/CLI process).
/// Provisional contract (spec §9.3): unload is deliberately absent — the supervisor recycles the worker process
/// because in-process unload does not reliably return memory (measured, ADR-0001).
/// Future backends (e.g. a Qwen TTS adapter, possibly via a Python subprocess) implement this same interface.
/// </summary>
public interface ITtsBackend : IDisposable
{
    string BackendId { get; }
    string BackendVersion { get; }
    TtsCapabilities Capabilities { get; }
    string ExecutionProvider { get; }
    bool IsLoaded { get; }
    Task LoadAsync(InstalledModel model, CancellationToken ct);
    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, string outputWavPath, CancellationToken ct);
}

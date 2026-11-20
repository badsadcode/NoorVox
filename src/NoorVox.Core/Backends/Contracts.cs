using NoorVox.Core.Models;

namespace NoorVox.Core.Backends;

/// <param name="Kind"><c>pack</c> (voice-pack file) · <c>blend</c> (Kokoro blend) · <c>built-in</c> (model speaker) · <c>clone</c> (voice profile).</param>
public sealed record VoiceInfo(string Id, string Language, string LanguageName, string Gender, string DisplayName, string PackId,
    string Kind = "pack", string? Description = null);

/// <param name="Style">Delivery directions; only for models whose capabilities allow it.</param>
/// <param name="Seed">Sampling seed; only for models that are reproducible with a seed.</param>
/// <param name="Language">Language name for engines that take one (Qwen: <c>auto</c>, <c>english</c>, ...).</param>
public sealed record SynthesisRequest(string Text, string VoiceId, double Speed, string? Style = null, long? Seed = null, string? Language = null);

/// <summary>Facts about a synthesized file, measured from the written audio.</summary>
public sealed record SynthesisResult(string OutputPath, int SampleRate, int Channels, int BitsPerSample, long Samples, long DurationMs, long ElapsedMs,
    long? Seed = null);

/// <summary>GPU memory as reported by the engine itself (never estimated).</summary>
public sealed record GpuMemoryInfo(string Device, long AllocatedBytes, long ReservedBytes, long PeakAllocatedBytes,
    long DeviceFreeBytes, long DeviceTotalBytes, string Source, DateTimeOffset SampledAt);

/// <summary>A helper process a backend runs (the Qwen Python sidecar), so the Monitor can show its real footprint.</summary>
public sealed record SidecarInfo(int Pid, string Description, long WorkingSetBytes, long PrivateBytes, DateTimeOffset SampledAt);

/// <summary>Optional: backends with resources outside the worker process report them here.</summary>
public interface IBackendResources
{
    GpuMemoryInfo? Gpu { get; }
    SidecarInfo? Sidecar { get; }
    /// <summary>A non-fatal note about the provider (e.g. CUDA requested but unavailable, fell back to CPU).</summary>
    string? ProviderNote { get; }
}

/// <summary>
/// One inference implementation, hosted inside an isolated worker process (never in the GUI/CLI process).
/// Provisional contract (spec §9.3): unload is deliberately absent — the supervisor recycles the worker process
/// because in-process unload does not reliably return memory (measured, ADR-0001).
/// The Qwen adapter implements it with a Python sidecar process (ADR-0002).
/// </summary>
/// <summary>Where the worker finds things a backend needs beyond the model folder. Computed by the supervisor from settings.</summary>
public sealed record BackendLoadOptions(
    string? EngineDirectory = null, string? SidecarScript = null, string? CacheDirectory = null,
    string ExecutionProvider = "cpu", string? OrtCudaDirectory = null, IReadOnlyList<string>? CudaLibraryDirectories = null);

public interface ITtsBackend : IDisposable
{
    string BackendId { get; }
    string BackendVersion { get; }
    string ExecutionProvider { get; }
    bool IsLoaded { get; }
    Task LoadAsync(InstalledModel model, BackendLoadOptions options, CancellationToken ct);
    Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, string outputWavPath, CancellationToken ct);
}

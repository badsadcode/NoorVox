namespace NoorVox.Core.Models;

/// <summary>
/// What a model can really do, declared in the catalog and copied into its manifest. The CLI and the studio show
/// only the controls that are <c>true</c> here (AGENTS.md rule 2), and requests that ask for more are rejected.
/// </summary>
public sealed record ModelCapabilities
{
    /// <summary>A speed multiplier the engine honours.</summary>
    public bool Speed { get; init; }
    public double MinSpeed { get; init; } = 1.0;
    public double MaxSpeed { get; init; } = 1.0;
    /// <summary>Free-text delivery directions ("calm, warm, a hint of humour").</summary>
    public bool Style { get; init; }
    /// <summary>Speaks in the voice of a reference recording (a voice profile).</summary>
    public bool Cloning { get; init; }
    /// <summary>Makes a new voice from a written description (Qwen VoiceDesign). The description goes in the style field and is required.</summary>
    public bool Design { get; init; }
    /// <summary>Kokoro voice blending (weighted average of style vectors).</summary>
    public bool Blending { get; init; }
    /// <summary>A seed makes a take reproducible on the same GPU and software.</summary>
    public bool Seed { get; init; }
    public bool RequiresGpu { get; init; }
    /// <summary>Values accepted for the language option, e.g. <c>auto</c>, <c>english</c>. Empty = the voice decides.</summary>
    public List<string> Languages { get; init; } = [];

    /// <summary>Kokoro manifests from 0.1/0.2 have no capabilities block; this is what Kokoro supports.</summary>
    public static readonly ModelCapabilities Kokoro = new()
    {
        Speed = true, MinSpeed = 0.5, MaxSpeed = 2.0, Blending = true,
    };

    public static ModelCapabilities For(PackageManifest manifest) =>
        manifest.Capabilities ?? (manifest.Backend is null or "kokoro-onnx" ? Kokoro : new ModelCapabilities());
}

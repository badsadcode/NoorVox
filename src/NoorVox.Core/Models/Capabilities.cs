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
    /// <summary>An emotion-intensity slider (Chatterbox "exaggeration"): low = flat and calm, high = dramatic.</summary>
    public bool Emotion { get; init; }
    public double EmotionMin { get; init; }
    public double EmotionMax { get; init; }
    public double EmotionDefault { get; init; }
    /// <summary>A pace/steadiness slider (Chatterbox "cfg weight"): lower = slower, more deliberate delivery.</summary>
    public bool Pace { get; init; }
    public double PaceMin { get; init; }
    public double PaceMax { get; init; }
    public double PaceDefault { get; init; }
    /// <summary>
    /// Inline tags the model was trained to act on, typed into the text (Chatterbox Turbo: <c>[laugh]</c>, <c>[sigh]</c>,
    /// <c>[whispering]</c>…). Taken from the model's own tokenizer file and documented by its authors; empty = no tags.
    /// </summary>
    public List<string> Tags { get; init; } = [];
    /// <summary>The engine marks its audio with an inaudible watermark (Chatterbox: Resemble AI "Perth"); null = none.</summary>
    public string? Watermark { get; init; }
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

    /// <summary>
    /// The catalog is the authority for models it knows (same id and version): a manifest keeps the capabilities from install
    /// time, so corrections in a newer NoorVox (e.g. Chatterbox's pace minimum, raised from 0 after 0 turned out to crash the
    /// model) reach models that are already installed. Imported or unknown models fall back to their manifest.
    /// </summary>
    public static ModelCapabilities For(PackageManifest manifest) =>
        CatalogCapabilities(manifest.Id, manifest.Version)
        ?? manifest.Capabilities
        ?? (manifest.Backend is null or "kokoro-onnx" ? Kokoro : new ModelCapabilities());

    static ModelCapabilities? CatalogCapabilities(string id, string version) =>
        id.Length == 0 ? null : ModelCatalog.Embedded?.Models.FirstOrDefault(m => m.Id == id && m.Version == version)?.Capabilities;
}

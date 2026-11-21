using NoorVox.Core.Backends;
using NoorVox.Core.Storage;
using NoorVox.Core.Util;

namespace NoorVox.Core.Models;

/// <summary>
/// Written last when a package is installed, so its presence marks a complete install.
/// Models: <c>&lt;models&gt;/&lt;id&gt;/&lt;version&gt;/noorvox.model.json</c>.
/// Voice packs: <c>&lt;model dir&gt;/voicepacks/&lt;packId&gt;.json</c>.
/// </summary>
public sealed class PackageManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "model"; // "model" | "voicePack"
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Backend { get; set; }
    public string? ModelId { get; set; }
    public string? ModelVersion { get; set; }
    public string License { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    public string Source { get; set; } = "";
    public string Origin { get; set; } = "download"; // "download" | "import"
    public string? ImportedFrom { get; set; }
    public string CatalogVersion { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    public string? DefaultVoice { get; set; }
    public int? SampleRate { get; set; }
    /// <summary>null on Kokoro manifests written before 0.3 (see <see cref="ModelCapabilities.For"/>).</summary>
    public ModelCapabilities? Capabilities { get; set; }
    public List<BuiltInVoice>? BuiltInVoices { get; set; }
    /// <summary>Engine pack the model runs on (e.g. <c>qwen-tts</c>); null = built into NoorVox (Kokoro).</summary>
    public string? Engine { get; set; }
    public List<PackageFile> Files { get; set; } = [];
}

public sealed record InstalledModel(PackageManifest Manifest, string Directory, IReadOnlyList<PackageManifest> VoicePacks,
    IReadOnlyList<VoiceProfile>? Profiles = null)
{
    /// <summary>Pseudo pack ids for voices that don't come from a voice-pack file.</summary>
    public const string BuiltInPackId = "built-in", ProfilePackId = "voice-profiles";

    public string Id => Manifest.Id;
    public string Version => Manifest.Version;
    public string Reference => $"{Id}@{Version}";
    public ModelCapabilities Capabilities => ModelCapabilities.For(Manifest);
    public string Backend => Manifest.Backend ?? "kokoro-onnx";

    /// <summary>The main weights file: Kokoro's <c>.onnx</c>, else <c>model.safetensors</c>, else the largest file.</summary>
    public PackageFile PrimaryFile =>
        Manifest.Files.FirstOrDefault(f => f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        ?? Manifest.Files.FirstOrDefault(f => f.Path == "model.safetensors")
        ?? Manifest.Files.OrderByDescending(f => f.Size).First();

    /// <summary>sha256 of the model weights file (provenance for takes).</summary>
    public string ModelSha256 => PrimaryFile.Sha256;
    public string ModelFile => Path.Combine(Directory, PrimaryFile.Path);

    /// <summary>
    /// Every voice this model can speak with: voice-pack files (Kokoro <c>voices/*.npy</c>), built-in speakers (Qwen
    /// CustomVoice) and voice profiles (cloned voices, for models that can clone). <c>File</c> is null unless the
    /// voice is a voice-pack file.
    /// </summary>
    public IReadOnlyList<(string VoiceId, string PackId, PackageFile? File)> Voices =>
        VoicePacks.SelectMany(p => p.Files
                .Where(f => f.Path.StartsWith("voices/", StringComparison.Ordinal) && f.Path.EndsWith(".npy", StringComparison.Ordinal))
                .Select(f => (Path.GetFileNameWithoutExtension(f.Path), p.Id, (PackageFile?)f)))
            .Concat((Manifest.BuiltInVoices ?? []).Select(v => (v.Id, BuiltInPackId, (PackageFile?)null)))
            .Concat(Capabilities.Cloning ? (Profiles ?? []).Select(p => (p.VoiceId, ProfilePackId, (PackageFile?)null)) : [])
            .GroupBy(v => v.Item1).Select(g => g.First())
            .OrderBy(v => v.Item1, StringComparer.Ordinal).ToList();

    public bool HasVoice(string voiceId) => Voices.Any(v => v.VoiceId == voiceId);
}

public sealed record VerifyProblem(string Path, string Problem);
public sealed record VerifyResult(bool Ok, string Mode, int CheckedFiles, IReadOnlyList<VerifyProblem> Problems);

/// <summary>Reads what is installed under the models directory. Never downloads anything.</summary>
public sealed class ModelRegistry(StorageContext storage)
{
    public const string ModelManifestName = "noorvox.model.json";

    public IReadOnlyList<InstalledModel> List()
    {
        var root = storage.ModelsDir;
        if (!Directory.Exists(root)) return [];
        var result = new List<InstalledModel>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, ModelManifestName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }))
        {
            try
            {
                var manifest = Json.ReadFile<PackageManifest>(manifestPath);
                var dir = Path.GetDirectoryName(manifestPath)!;
                result.Add(new InstalledModel(manifest, dir, ReadVoicePacks(dir), VoiceProfiles.ReadAll(root)));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
            {
                // A corrupt manifest means "not installed"; doctor reports it.
            }
        }
        return result.OrderBy(m => m.Id).ThenByDescending(m => ModelRef.ParseVersion(m.Version)).ToList();
    }

    /// <summary>Reads one installed package directory (used by the worker, which receives only a path).</summary>
    public static InstalledModel ReadPackage(string modelDir)
    {
        var manifestPath = Path.Combine(modelDir, ModelManifestName);
        if (!File.Exists(manifestPath))
            throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, $"No {ModelManifestName} in {modelDir}.");
        // Package layout is <models>/<id>/<version>, so the models root is two levels up (voice profiles live there).
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(modelDir)))!;
        return new InstalledModel(Json.ReadFile<PackageManifest>(manifestPath), modelDir, ReadVoicePacks(modelDir), VoiceProfiles.ReadAll(root));
    }

    static List<PackageManifest> ReadVoicePacks(string modelDir)
    {
        var dir = Path.Combine(modelDir, "voicepacks");
        if (!Directory.Exists(dir)) return [];
        var packs = new List<PackageManifest>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try { packs.Add(Json.ReadFile<PackageManifest>(f)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException) { }
        }
        return packs;
    }

    public InstalledModel? Find(string reference)
    {
        var (id, version) = ModelRef.Parse(reference);
        var matches = List().Where(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return version is null ? matches.FirstOrDefault() : matches.FirstOrDefault(m => m.Version == version);
    }

    /// <summary>Resolves an explicit reference, then the configured default, then the only/first installed model.</summary>
    public InstalledModel Resolve(string? reference)
    {
        reference ??= storage.Settings.DefaultModel;
        if (reference is not null)
        {
            return Find(reference) ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing,
                $"Model '{reference}' is not installed in {storage.ModelsDir}.",
                new { model = reference, modelsDir = storage.ModelsDir, hint = $"noorvox models install {ModelRef.Parse(reference).Id}" });
        }
        // No default set: the catalog's recommended model (Kokoro: fast, CPU, no load wait) beats alphabetical order, so installing
        // a GPU engine like Chatterbox or Qwen never silently becomes everyone's default.
        var installed = List();
        var recommended = ModelCatalog.Embedded?.Models.Where(m => m.Recommended).Select(m => m.Id).ToHashSet() ?? [];
        return installed.FirstOrDefault(m => recommended.Contains(m.Id)) ?? installed.FirstOrDefault()
            ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing,
                $"No voice model is installed in {storage.ModelsDir}.",
                new { modelsDir = storage.ModelsDir, hint = "noorvox models install kokoro-82m" });
    }

    /// <summary><paramref name="full"/> = sha256 every file; otherwise existence + size only (fast).</summary>
    public static VerifyResult Verify(string packageDir, IEnumerable<PackageFile> files, bool full, Action<string>? onFile = null)
    {
        var problems = new List<VerifyProblem>();
        int count = 0;
        foreach (var f in files)
        {
            count++;
            onFile?.Invoke(f.Path);
            var path = Path.Combine(packageDir, f.Path);
            if (!File.Exists(path)) { problems.Add(new(f.Path, "missing")); continue; }
            var len = new FileInfo(path).Length;
            if (len != f.Size) { problems.Add(new(f.Path, $"size {len} != expected {f.Size}")); continue; }
            if (full && !FileUtil.Sha256File(path).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add(new(f.Path, "sha256 mismatch"));
        }
        return new VerifyResult(problems.Count == 0, full ? "sha256" : "size", count, problems);
    }
}

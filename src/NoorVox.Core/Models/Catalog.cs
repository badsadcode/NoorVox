namespace NoorVox.Core.Models;

public sealed record PackageFile(string Path, long Size, string Sha256);

/// <summary>How to obtain files: a single file, or a zip whose entries are filtered to the package's file list.</summary>
public sealed record CatalogDownload(string Url, long Size, string Sha256, string Kind, string Target);

/// <summary>Anything installable from the catalog (a model or a voice pack).</summary>
public interface ICatalogItem
{
    string Id { get; }
    string Version { get; }
    string DisplayName { get; }
    string License { get; }
    string LicenseUrl { get; }
    string Source { get; }
    List<CatalogDownload> Downloads { get; }
    List<PackageFile> Files { get; }
}

public sealed record CatalogModel(
    string Id, string Version, string DisplayName, string Description, string Backend, bool Recommended,
    string License, string LicenseUrl, string Source, string DefaultVoice, int SampleRate,
    List<string> DefaultVoicePacks, List<CatalogDownload> Downloads, List<PackageFile> Files) : ICatalogItem;

public sealed record CatalogVoicePack(
    string Id, string Version, string ModelId, List<string> ModelVersions, string DisplayName, string Description,
    string License, string LicenseUrl, string Source, List<CatalogDownload> Downloads, List<PackageFile> Files) : ICatalogItem;

/// <summary>
/// The list of models and voice packs NoorVox knows how to install, with URLs, sizes, sha256 and licenses.
/// v1 ships embedded in NoorVox.Core (regenerate with tools/gen_catalog.py); a signed remote refresh comes later.
/// </summary>
public sealed record ModelCatalog(int CatalogSchemaVersion, string CatalogVersion, List<CatalogModel> Models, List<CatalogVoicePack> VoicePacks)
{
    public static ModelCatalog LoadEmbedded()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("NoorVox.Core.catalog.json")
            ?? throw new InvalidOperationException("Embedded catalog missing.");
        using var reader = new StreamReader(stream);
        var catalog = Json.Deserialize<ModelCatalog>(reader.ReadToEnd()) ?? throw new InvalidDataException("Empty catalog.");
        if (catalog.CatalogSchemaVersion != 1) throw new InvalidDataException($"Unsupported catalog schema {catalog.CatalogSchemaVersion}.");
        return catalog;
    }

    /// <summary>Finds a model by <c>id</c> (latest version) or <c>id@version</c>.</summary>
    public CatalogModel? FindModel(string reference)
    {
        var (id, version) = ModelRef.Parse(reference);
        var matches = Models.Where(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return version is null
            ? matches.OrderByDescending(m => ModelRef.ParseVersion(m.Version)).FirstOrDefault()
            : matches.FirstOrDefault(m => m.Version == version);
    }

    public CatalogVoicePack? FindVoicePack(string reference)
    {
        var (id, version) = ModelRef.Parse(reference);
        var matches = VoicePacks.Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return version is null
            ? matches.OrderByDescending(p => ModelRef.ParseVersion(p.Version)).FirstOrDefault()
            : matches.FirstOrDefault(p => p.Version == version);
    }

    public IEnumerable<CatalogVoicePack> VoicePacksFor(string modelId, string modelVersion) =>
        VoicePacks.Where(p => p.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase) && p.ModelVersions.Contains(modelVersion));
}

public static class ModelRef
{
    /// <summary>Parses <c>kokoro-82m</c> or <c>kokoro-82m@1.0</c>.</summary>
    public static (string Id, string? Version) Parse(string reference)
    {
        var at = reference.IndexOf('@');
        return at < 0 ? (reference.Trim(), null) : (reference[..at].Trim(), reference[(at + 1)..].Trim());
    }

    public static Version ParseVersion(string v) => System.Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);
}

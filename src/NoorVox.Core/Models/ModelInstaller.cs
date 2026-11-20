using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using NoorVox.Core.Storage;
using NoorVox.Core.Util;

namespace NoorVox.Core.Models;

public sealed record InstallProgress(string Stage, string Item, string? File, long Done, long Total);

public sealed record InstallResult(
    string Kind, string Id, string Version, string Status, string Path, long BytesDownloaded,
    string License, string Source, IReadOnlyList<InstallResult>? VoicePacks = null, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Installs catalog models and voice packs (download → staging under the cache dir → sha256 verify → move into the
/// models dir → manifest written last), and imports packages already on disk by matching them against catalog hashes.
/// </summary>
public sealed class ModelInstaller(StorageContext storage, ModelCatalog catalog, HttpClient? http = null)
{
    static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoorVox", NoorVoxInfo.Version));
        return c;
    });

    readonly HttpClient _http = http ?? SharedHttp.Value;
    readonly ModelRegistry _registry = new(storage);

    public string PackageDir(string id, string version) => Path.Combine(storage.ModelsDir, id, version);

    public async Task<InstallResult> InstallModelAsync(CatalogModel model, bool withDefaultVoicePacks, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var dir = PackageDir(model.Id, model.Version);
        var manifestPath = Path.Combine(dir, ModelRegistry.ModelManifestName);
        long downloaded = 0;
        string status = "alreadyInstalled";

        bool installed = File.Exists(manifestPath) && ModelRegistry.Verify(dir, model.Files, full: false).Ok;
        if (!installed)
        {
            EnsureSpace(model);
            downloaded = await FetchAndPlaceAsync(model, dir, progress, ct);
            var manifest = ToManifest(model, "model", origin: "download");
            manifest.Backend = model.Backend;
            manifest.DefaultVoice = model.DefaultVoice;
            manifest.SampleRate = model.SampleRate;
            FileUtil.WriteAllTextAtomic(manifestPath, Json.Serialize(manifest));
            status = "installed";
        }

        var packs = new List<InstallResult>();
        if (withDefaultVoicePacks)
        {
            var target = _registry.Find($"{model.Id}@{model.Version}")!;
            foreach (var packId in model.DefaultVoicePacks)
            {
                var pack = catalog.FindVoicePack(packId) ?? throw new InvalidDataException($"Catalog references unknown voice pack {packId}.");
                packs.Add(await InstallVoicePackAsync(pack, target, progress, ct));
            }
        }
        return new InstallResult("model", model.Id, model.Version, status, dir, downloaded + packs.Sum(p => p.BytesDownloaded),
            model.License, model.Source, packs);
    }

    public async Task<InstallResult> InstallVoicePackAsync(CatalogVoicePack pack, InstalledModel target, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (!pack.ModelId.Equals(target.Id, StringComparison.OrdinalIgnoreCase) || !pack.ModelVersions.Contains(target.Version))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Voice pack {pack.Id} is for {pack.ModelId} {string.Join("/", pack.ModelVersions)}, not {target.Reference}.");

        var manifestPath = Path.Combine(target.Directory, "voicepacks", $"{pack.Id}.json");
        if (File.Exists(manifestPath) && ModelRegistry.Verify(target.Directory, pack.Files, full: false).Ok)
            return new InstallResult("voicePack", pack.Id, pack.Version, "alreadyInstalled", target.Directory, 0, pack.License, pack.Source);

        EnsureSpace(pack);
        long downloaded = await FetchAndPlaceAsync(pack, target.Directory, progress, ct);
        var manifest = ToManifest(pack, "voicePack", origin: "download");
        manifest.ModelId = target.Id;
        manifest.ModelVersion = target.Version;
        FileUtil.WriteAllTextAtomic(manifestPath, Json.Serialize(manifest));
        return new InstallResult("voicePack", pack.Id, pack.Version, "installed", target.Directory, downloaded, pack.License, pack.Source);
    }

    /// <summary>
    /// Imports a model folder already on disk. Identity, license and source come from matching file hashes against the
    /// catalog — unknown weights are rejected rather than guessed. Voice packs found complete in the folder are imported too.
    /// </summary>
    public Task<InstallResult> ImportAsync(string sourceDir, IProgress<InstallProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(sourceDir))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Folder not found: {sourceDir}");

        var onnxFiles = Directory.GetFiles(sourceDir, "*.onnx", SearchOption.TopDirectoryOnly);
        CatalogModel? model = null;
        foreach (var f in onnxFiles)
        {
            ct.ThrowIfCancellationRequested();
            var len = new FileInfo(f).Length;
            var candidates = catalog.Models.Where(m => m.Files.Any(cf => cf.Path.EndsWith(".onnx") && cf.Size == len)).ToList();
            if (candidates.Count == 0) continue;
            progress?.Report(new InstallProgress("hashing", "import", Path.GetFileName(f), 0, len));
            var sha = FileUtil.Sha256File(f, done => progress?.Report(new InstallProgress("hashing", "import", Path.GetFileName(f), done, len)));
            model = candidates.FirstOrDefault(m => m.Files.Any(cf => cf.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase)));
            if (model is not null)
            {
                var dir = PackageDir(model.Id, model.Version);
                var modelFile = model.Files.First(cf => cf.Path.EndsWith(".onnx"));
                CopyVerified(f, Path.Combine(dir, modelFile.Path), modelFile);
                var manifest = ToManifest(model, "model", origin: "import");
                manifest.ImportedFrom = sourceDir;
                manifest.Backend = model.Backend;
                manifest.DefaultVoice = model.DefaultVoice;
                manifest.SampleRate = model.SampleRate;
                FileUtil.WriteAllTextAtomic(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(manifest));
                break;
            }
        }
        if (model is null)
            throw new NoorVoxException(ErrorCodes.UnknownModel, ExitCode.ModelMissing,
                $"No model in {sourceDir} matches a known catalog hash. NoorVox only imports verified models.",
                new { sourceDir, onnxFiles });

        var dirPath = PackageDir(model.Id, model.Version);
        var packs = new List<InstallResult>();
        var warnings = new List<string>();
        foreach (var pack in catalog.VoicePacksFor(model.Id, model.Version))
        {
            var present = pack.Files.Count(pf => File.Exists(Path.Combine(sourceDir, pf.Path)));
            if (present == 0) continue;
            if (present < pack.Files.Count)
            {
                warnings.Add($"Voice pack {pack.Id}: only {present}/{pack.Files.Count} files present in source; skipped (install it with `noorvox voices install {pack.Id}`).");
                continue;
            }
            try
            {
                foreach (var pf in pack.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new InstallProgress("copying", pack.Id, pf.Path, 0, pf.Size));
                    CopyVerified(Path.Combine(sourceDir, pf.Path), Path.Combine(dirPath, pf.Path), pf);
                }
            }
            catch (NoorVoxException ex) when (ex.Code == ErrorCodes.VerificationFailed)
            {
                warnings.Add($"Voice pack {pack.Id}: {ex.Message}; skipped.");
                continue;
            }
            var pm = ToManifest(pack, "voicePack", origin: "import");
            pm.ImportedFrom = sourceDir;
            pm.ModelId = model.Id;
            pm.ModelVersion = model.Version;
            FileUtil.WriteAllTextAtomic(Path.Combine(dirPath, "voicepacks", $"{pack.Id}.json"), Json.Serialize(pm));
            packs.Add(new InstallResult("voicePack", pack.Id, pack.Version, "imported", dirPath, 0, pack.License, pack.Source));
        }
        return new InstallResult("model", model.Id, model.Version, "imported", dirPath, 0, model.License, model.Source, packs, warnings);
    }, ct);

    static void CopyVerified(string source, string destination, PackageFile expected)
    {
        if (new FileInfo(source).Length != expected.Size || !FileUtil.Sha256File(source).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new NoorVoxException(ErrorCodes.VerificationFailed, ExitCode.DownloadFailed, $"{Path.GetFileName(source)} does not match the catalog hash.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && new FileInfo(destination).Length == expected.Size
            && FileUtil.Sha256File(destination).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) return;
        var tmp = destination + ".importing";
        File.Copy(source, tmp, overwrite: true);
        File.Move(tmp, destination, overwrite: true);
    }

    PackageManifest ToManifest(ICatalogItem item, string kind, string origin) => new()
    {
        Kind = kind,
        Id = item.Id,
        Version = item.Version,
        DisplayName = item.DisplayName,
        License = item.License,
        LicenseUrl = item.LicenseUrl,
        Source = item.Source,
        Origin = origin,
        CatalogVersion = catalog.CatalogVersion,
        InstalledAt = DateTimeOffset.UtcNow,
        Files = item.Files,
    };

    void EnsureSpace(ICatalogItem item)
    {
        long need = item.Downloads.Sum(d => d.Size) + item.Files.Sum(f => f.Size);
        foreach (var dir in new[] { storage.CacheDir, storage.ModelsDir }.Distinct())
        {
            var free = FileUtil.FreeBytes(dir);
            if (free is not null && free < need)
                throw new NoorVoxException(ErrorCodes.IoError, ExitCode.IoError,
                    $"Not enough free space on {Path.GetPathRoot(dir)} for {item.DisplayName}: need ~{need / 1048576} MB, have {free / 1048576} MB.",
                    new { path = dir, needBytes = need, freeBytes = free });
        }
    }

    /// <summary>Downloads every source of <paramref name="item"/>, verifies, and places its files in <paramref name="packageDir"/>.</summary>
    async Task<long> FetchAndPlaceAsync(ICatalogItem item, string packageDir, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(storage.CacheDir, "staging", $"{item.Id}-{item.Version}");
        Directory.CreateDirectory(staging);
        long downloaded = 0;
        var wanted = item.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var assembled = Path.Combine(staging, "assembled");
        if (Directory.Exists(assembled)) Directory.Delete(assembled, recursive: true);
        Directory.CreateDirectory(assembled);

        foreach (var d in item.Downloads)
        {
            var name = Path.GetFileName(new Uri(d.Url).AbsolutePath);
            var local = Path.Combine(staging, name);
            downloaded += await DownloadVerifiedAsync(item.Id, d, local, progress, ct);
            progress?.Report(new InstallProgress("unpacking", item.Id, name, 0, 0));
            if (d.Kind == "file")
            {
                File.Move(local, Path.Combine(assembled, d.Target), overwrite: true);
            }
            else if (d.Kind == "zip")
            {
                using (var zip = ZipFile.OpenRead(local))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var rel = Path.GetRelativePath(".", Path.Combine(d.Target, entry.FullName)).Replace('\\', '/');
                        if (!wanted.ContainsKey(rel)) continue; // only files the catalog lists (also blocks zip-slip)
                        var dest = Path.Combine(assembled, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }
                File.Delete(local); // archive must be closed before deleting
            }
            else throw new InvalidDataException($"Unknown download kind '{d.Kind}'.");
        }

        var verify = ModelRegistry.Verify(assembled, item.Files, full: true,
            f => progress?.Report(new InstallProgress("verifying", item.Id, f, 0, 0)));
        if (!verify.Ok)
            throw new NoorVoxException(ErrorCodes.VerificationFailed, ExitCode.DownloadFailed,
                $"{item.DisplayName}: {verify.Problems.Count} file(s) failed verification.", verify);

        foreach (var f in item.Files)
        {
            var dest = Path.Combine(packageDir, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(Path.Combine(assembled, f.Path), dest, overwrite: true);
        }
        Directory.Delete(staging, recursive: true);
        return downloaded;
    }

    /// <summary>Resumable download (HTTP Range) into <c>*.part</c>, then size + sha256 check. Returns bytes transferred.</summary>
    async Task<long> DownloadVerifiedAsync(string item, CatalogDownload d, string finalPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var name = Path.GetFileName(finalPath);
        if (File.Exists(finalPath) && new FileInfo(finalPath).Length == d.Size && FileUtil.Sha256File(finalPath) == d.Sha256) return 0;

        var part = finalPath + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing > d.Size) { File.Delete(part); existing = 0; }
        long transferred = 0;

        if (existing < d.Size)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, d.Url);
                if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (existing > 0 && resp.StatusCode == HttpStatusCode.OK) existing = 0; // server ignored Range: start over
                if (!resp.IsSuccessStatusCode)
                    throw new NoorVoxException(ErrorCodes.DownloadFailed, ExitCode.DownloadFailed,
                        $"Download of {name} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.", new { url = d.Url });

                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var buffer = new byte[1 << 16];
                long done = existing;
                var lastReport = DateTime.MinValue;
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(TimeSpan.FromSeconds(60)); // a dead connection must not hang forever
                    int read;
                    try { read = await body.ReadAsync(buffer, stall.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new NoorVoxException(ErrorCodes.DownloadFailed, ExitCode.DownloadFailed,
                            $"Download of {name} stalled for 60 s. Run the same command again to resume.", new { url = d.Url, bytesSoFar = done });
                    }
                    if (read == 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    transferred += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                    {
                        progress?.Report(new InstallProgress("downloading", item, name, done, d.Size));
                        lastReport = DateTime.UtcNow;
                    }
                }
                progress?.Report(new InstallProgress("downloading", item, name, done, d.Size));
            }
            catch (HttpRequestException ex)
            {
                throw new NoorVoxException(ErrorCodes.DownloadFailed, ExitCode.DownloadFailed,
                    $"Download of {name} failed: {ex.Message}. Run the same command again to resume.", new { url = d.Url }, ex);
            }
        }

        var size = new FileInfo(part).Length;
        progress?.Report(new InstallProgress("verifying", item, name, 0, size));
        var sha = FileUtil.Sha256File(part);
        if (size != d.Size || !sha.Equals(d.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new NoorVoxException(ErrorCodes.VerificationFailed, ExitCode.DownloadFailed,
                $"{name} failed verification (size {size}/{d.Size}, sha256 {sha}). The partial file was deleted.", new { url = d.Url, expectedSha256 = d.Sha256 });
        }
        File.Move(part, finalPath, overwrite: true);
        return transferred;
    }
}

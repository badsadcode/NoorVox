using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Util;

namespace NoorVox.Core.Engines;

/// <summary>
/// An installable runtime a model needs beyond NoorVox itself (ADR-0002):
/// <c>python</c> = a private Python plus a hash-locked set of pip packages (Qwen3-TTS);
/// <c>native</c> = DLLs from a verified archive (ONNX Runtime's CUDA provider for Kokoro on the GPU).
/// </summary>
public sealed record CatalogEngine(
    string Id, string Version, string DisplayName, string Description, string Kind,
    string License, string LicenseUrl, string Source, List<CatalogDownload> Downloads, List<PackageFile> Files,
    string? Lock = null, List<string>? PipIndexUrls = null, long ApproxInstallBytes = 0, List<string>? Requires = null) : ICatalogItem;

/// <summary><c>noorvox.engine.json</c>, written last: its presence marks a complete engine install.</summary>
public sealed class EngineManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string License { get; set; } = "";
    public string Source { get; set; } = "";
    public string CatalogVersion { get; set; } = "";
    public string? LockSha256 { get; set; }
    /// <summary>What the verification run reported (python, torch, cuda, device), as measured on this PC.</summary>
    public Dictionary<string, JsonElement>? Verified { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

public sealed record InstalledEngine(EngineManifest Manifest, string Directory)
{
    public string PythonExe => Path.Combine(Directory, "python", "python.exe");
    /// <summary>CUDA 12 + cuDNN 9 DLLs that ship inside the PyTorch wheel; Kokoro's CUDA provider borrows them.</summary>
    public string TorchLibDir => Path.Combine(Directory, "python", "Lib", "site-packages", "torch", "lib");
    public string OrtNativeDir => Path.Combine(Directory, "runtimes", "win-x64", "native");
}

public sealed record EngineStatus(
    string Id, string Version, string DisplayName, string Description, string Kind, string State, string Path,
    long ApproxInstallBytes, string License, string Source, IReadOnlyList<string> Requires, IReadOnlyList<string> UsedBy,
    Dictionary<string, JsonElement>? Verified);

public static class EngineIds
{
    public const string QwenTts = "qwen-tts";
    public const string OrtCuda = "onnxruntime-cuda";
}

/// <summary>Reads installed engines (<c>&lt;models&gt;/engines/&lt;id&gt;/&lt;version&gt;</c>). Never downloads.</summary>
public sealed class EngineRegistry(StorageContext storage)
{
    public const string ManifestName = "noorvox.engine.json";
    public string Root => Path.Combine(storage.ModelsDir, "engines");
    public string EngineDir(string id, string version) => Path.Combine(Root, id, version);

    public InstalledEngine? Find(string id)
    {
        var dir = Path.Combine(Root, id);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir)
            .Select(d => Path.Combine(d, ManifestName))
            .Where(File.Exists)
            .Select(m =>
            {
                try { return new InstalledEngine(Json.ReadFile<EngineManifest>(m), Path.GetDirectoryName(m)!); }
                catch (Exception ex) when (ex is JsonException or IOException) { return null; }
            })
            .Where(e => e is not null && e.Manifest.Id == id)
            .OrderByDescending(e => ModelRef.ParseVersion(e!.Manifest.Version))
            .FirstOrDefault();
    }

    public InstalledEngine Require(string id, string forWhat) => Find(id) ?? throw new NoorVoxException(ErrorCodes.EngineNotInstalled, ExitCode.ModelMissing,
        $"{forWhat} needs the '{id}' engine pack, which is not installed.", new { engine = id, hint = $"noorvox engines install {id}" });

    public IReadOnlyList<EngineStatus> List(ModelCatalog catalog)
    {
        var models = new ModelRegistry(storage).List();
        return (catalog.Engines ?? []).Select(e =>
        {
            var inst = Find(e.Id);
            var state = inst is null ? (Directory.Exists(EngineDir(e.Id, e.Version)) ? "incomplete" : "notInstalled")
                : inst.Manifest.Version == e.Version ? "installed" : "updateAvailable";
            var usedBy = catalog.Models.Where(m => m.Engine == e.Id).Select(m => m.Id)
                .Concat(e.Id == EngineIds.OrtCuda ? ["kokoro-82m (GPU option)"] : []).ToList();
            return new EngineStatus(e.Id, e.Version, e.DisplayName, e.Description, e.Kind, state, inst?.Directory ?? EngineDir(e.Id, e.Version),
                e.ApproxInstallBytes, e.License, e.Source, e.Requires ?? [], usedBy, inst?.Manifest.Verified);
        }).ToList();
    }
}

/// <summary>
/// Installs engine packs. Python engines: verified Python archive → engine folder → <c>pip install
/// --require-hashes --no-deps</c> of the embedded lock (every file sha256-pinned) → import + CUDA check → manifest →
/// rename. Interrupted installs resume (pip skips what is already installed; its cache lives in NoorVox's cache folder).
/// </summary>
public sealed class EngineInstaller(StorageContext storage, ModelCatalog catalog, HttpClient? http = null)
{
    readonly ModelInstaller _models = new(storage, catalog, http);
    readonly EngineRegistry _registry = new(storage);

    public async Task<InstallResult> InstallAsync(string engineId, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var engine = catalog.FindEngine(engineId) ?? throw new NoorVoxException(ErrorCodes.UnknownModel, ExitCode.ModelMissing,
            $"Unknown engine '{engineId}'. Known: {string.Join(", ", (catalog.Engines ?? []).Select(e => e.Id))}.");
        var results = new List<InstallResult>();
        foreach (var dep in engine.Requires ?? [])
            results.Add(await InstallAsync(dep, progress, ct));

        var dir = _registry.EngineDir(engine.Id, engine.Version);
        if (File.Exists(Path.Combine(dir, EngineRegistry.ManifestName)))
            return new InstallResult("engine", engine.Id, engine.Version, "alreadyInstalled", dir, 0, engine.License, engine.Source, results);

        _models.EnsureSpace(engine, engine.ApproxInstallBytes);
        // Installed in place; the manifest is written last and is the only "complete" marker (as for models). No final
        // folder rename: antivirus scanning thousands of fresh DLLs makes renaming a 5 GB tree fail with "access denied".
        var partial = dir;
        Directory.CreateDirectory(partial);
        long downloaded;
        var manifest = new EngineManifest
        {
            Id = engine.Id, Version = engine.Version, Kind = engine.Kind, DisplayName = engine.DisplayName, License = engine.License,
            Source = engine.Source, CatalogVersion = catalog.CatalogVersion,
        };
        if (engine.Kind == "native")
        {
            downloaded = await _models.FetchAndPlaceAsync(engine, partial, progress, ct);
        }
        else if (engine.Kind == "python")
        {
            downloaded = await InstallPythonAsync(engine, partial, progress, ct);
            var lockText = ReadLock(engine);
            manifest.LockSha256 = FileUtil.Sha256Text(lockText);
            progress?.Report(new InstallProgress("verifying", engine.Id, "import check", 0, 0));
            manifest.Verified = await VerifyPythonAsync(Path.Combine(partial, "python", "python.exe"), ct);
        }
        else throw new InvalidDataException($"Unknown engine kind '{engine.Kind}'.");

        manifest.InstalledAt = DateTimeOffset.UtcNow;
        FileUtil.WriteAllTextAtomic(Path.Combine(partial, EngineRegistry.ManifestName), Json.Serialize(manifest));
        var warnings = manifest.Verified is { } v && v.TryGetValue("cuda", out var cuda) && cuda.ValueKind == JsonValueKind.False
            ? new[] { "Installed, but PyTorch sees no CUDA GPU on this PC: Qwen voices need an NVIDIA GPU." } : null;
        return new InstallResult("engine", engine.Id, engine.Version, "installed", dir, downloaded, engine.License, engine.Source, results, warnings);
    }

    /// <summary>Deletes an installed engine pack. Engines are app-managed runtimes, never user data.</summary>
    public string Remove(string engineId)
    {
        var inst = _registry.Find(engineId) ?? throw new NoorVoxException(ErrorCodes.EngineNotInstalled, ExitCode.ModelMissing, $"Engine '{engineId}' is not installed.");
        Directory.Delete(inst.Directory, recursive: true);
        return inst.Directory;
    }

    static string ReadLock(CatalogEngine engine)
    {
        var name = $"NoorVox.Core.Engines.{engine.Lock}";
        using var stream = typeof(EngineInstaller).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Embedded lock {name} missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    async Task<long> InstallPythonAsync(CatalogEngine engine, string partial, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        long downloaded = 0;
        var staging = Path.Combine(storage.CacheDir, "staging", $"{engine.Id}-{engine.Version}");
        Directory.CreateDirectory(staging);
        foreach (var d in engine.Downloads)
        {
            if (d.Kind != "zip-tree") throw new InvalidDataException($"Python engines expect zip-tree downloads, not '{d.Kind}'.");
            var target = Path.GetFullPath(Path.Combine(partial, d.Target));
            if (File.Exists(Path.Combine(target, "python.exe"))) continue; // extracted by an earlier attempt
            var local = Path.Combine(staging, Path.GetFileName(new Uri(d.Url).AbsolutePath));
            downloaded += await _models.DownloadVerifiedAsync(engine.Id, d, local, progress, ct);
            progress?.Report(new InstallProgress("unpacking", engine.Id, Path.GetFileName(local), 0, 0));
            ExtractTree(local, d.Prefix ?? "", target + ".tmp");
            Directory.Move(target + ".tmp", target);
            File.Delete(local);
        }

        var lockPath = Path.Combine(partial, "requirements.lock");
        File.WriteAllText(lockPath, ReadLock(engine), new UTF8Encoding(false));
        var args = new List<string>
        {
            "-I", "-m", "pip", "install", "--require-hashes", "--no-deps", "--only-binary=:all:", "--no-warn-script-location",
            "--progress-bar", "off", "--disable-pip-version-check", "-r", lockPath,
        };
        var indexes = engine.PipIndexUrls ?? ["https://pypi.org/simple"];
        args.AddRange(["--index-url", indexes[0]]);
        foreach (var extra in indexes.Skip(1)) args.AddRange(["--extra-index-url", extra]);

        var log = Path.Combine(storage.LogsDir, $"engine-{engine.Id}.log");
        Directory.CreateDirectory(storage.LogsDir);
        int total = File.ReadLines(lockPath).Count(l => l.Contains("--hash=", StringComparison.Ordinal));
        int done = 0;
        bool installing = false;
        // The pip cache lives in NoorVox's configured cache folder, never in %LOCALAPPDATA%\pip.
        var env = new Dictionary<string, string> { ["PIP_CACHE_DIR"] = Path.Combine(storage.CacheDir, "pip") };
        int code = await RunAsync(Path.Combine(partial, "python", "python.exe"), args, env, log, line =>
        {
            if (line.StartsWith("Collecting ", StringComparison.Ordinal)) done++;
            if (line.Contains("Installing collected packages", StringComparison.Ordinal)) installing = true;
            var stage = installing ? "installing" : "downloading";
            progress?.Report(new InstallProgress(stage, engine.Id, line.Length > 160 ? line[..160] : line, done, total));
        }, ct);
        if (code != 0)
            throw new NoorVoxException(ErrorCodes.DownloadFailed, ExitCode.DownloadFailed,
                $"Installing the {engine.DisplayName} packages failed (pip exit {code}). Run the same command again to resume; details in {log}.",
                new { log });
        return downloaded;
    }

    /// <summary>Extracts every entry under <paramref name="prefix"/> into <paramref name="dest"/>, rejecting paths that escape it.</summary>
    internal static void ExtractTree(string archive, string prefix, string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var full = Path.GetFullPath(Path.Combine(dest, entry.FullName[prefix.Length..]));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry escapes the target folder: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
        }
    }

    const string VerifyScript =
        "import json, sys\n" +
        "import torch, faster_qwen3_tts, transformers\n" +
        "ok = torch.cuda.is_available()\n" +
        "print(json.dumps({'python': sys.version.split()[0], 'torch': torch.__version__, 'transformers': transformers.__version__,\n" +
        "  'cuda': ok, 'cudaVersion': torch.version.cuda, 'device': torch.cuda.get_device_name(0) if ok else None,\n" +
        "  'deviceMemoryBytes': torch.cuda.get_device_properties(0).total_memory if ok else None}))\n";

    /// <summary>Imports the engine in its own Python and reports what it sees (measured, ADR-0002).</summary>
    public static async Task<Dictionary<string, JsonElement>> VerifyPythonAsync(string pythonExe, CancellationToken ct)
    {
        var output = new StringBuilder();
        int code = await RunAsync(pythonExe, ["-I", "-c", VerifyScript], null, null, line => output.AppendLine(line), ct);
        var last = output.ToString().Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        if (code != 0 || !last.StartsWith('{'))
            throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"The engine does not import cleanly: {output.ToString().Trim()}");
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(last)!;
    }

    /// <summary>Runs a tool NoorVox installed (pip, the engine's Python), streaming merged output lines. Kills it on cancel.</summary>
    static async Task<int> RunAsync(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env, string? logPath,
        Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var log = logPath is null ? null : new StreamWriter(logPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        log?.WriteLine($"--- {DateTimeOffset.Now:u} {exe} {string.Join(' ', args)}");
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        proc.StandardInput.Close();
        var gate = new object();
        void Handle(string? line)
        {
            if (line is null) return;
            lock (gate) { log?.WriteLine(line); onLine(line); }
        }
        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // our own child, by handle
            throw;
        }
        return proc.ExitCode;
    }

}

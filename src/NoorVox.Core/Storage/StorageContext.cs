using NoorVox.Core.Util;

namespace NoorVox.Core.Storage;

/// <summary>A resolved storage location and where its value came from.</summary>
public sealed record ResolvedPath(StorageKind Kind, string Path, string Source);

/// <summary>
/// Resolves the NoorVox home, loads/saves settings and turns them into absolute storage paths.
/// C: is allowed: defaults are normal Windows locations, and every class can be redirected to any drive.
/// </summary>
public sealed class StorageContext
{
    public const string HomeEnvVar = "NOORVOX_HOME";
    public const string PortableMarker = "noorvox.portable";

    public string Home { get; }
    public HomeMode Mode { get; }
    public string SettingsPath => Path.Combine(Home, "settings.json");
    /// <summary>Small runtime state (worker ownership registry). Lives in the home, not in bulk storage.</summary>
    public string RuntimeDir => Path.Combine(Home, "runtime");
    public string WorkerRegistryDir => Path.Combine(RuntimeDir, "workers");
    public NoorVoxSettings Settings { get; private set; }

    StorageContext(string home, HomeMode mode, NoorVoxSettings settings)
    {
        Home = home;
        Mode = mode;
        Settings = settings;
    }

    /// <summary>Loads settings for the current process. Missing settings file = defaults (nothing is written).</summary>
    public static StorageContext Load(string? exeDirectory = null)
    {
        var (home, mode) = ResolveHome(exeDirectory ?? AppContext.BaseDirectory);
        var path = Path.Combine(home, "settings.json");
        NoorVoxSettings settings;
        try
        {
            settings = File.Exists(path) ? Json.ReadFile<NoorVoxSettings>(path) : new NoorVoxSettings();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            throw new NoorVoxException(ErrorCodes.ConfigError, ExitCode.ConfigError, $"Settings file is not valid JSON: {path}", new { path }, ex);
        }
        if (settings.SchemaVersion != 1)
            throw new NoorVoxException(ErrorCodes.ConfigError, ExitCode.ConfigError,
                $"Settings schemaVersion {settings.SchemaVersion} is not supported by NoorVox {NoorVoxInfo.Version}.", new { path });
        return new StorageContext(home, mode, settings);
    }

    static (string home, HomeMode mode) ResolveHome(string exeDirectory)
    {
        var env = Environment.GetEnvironmentVariable(HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return (Path.GetFullPath(env), HomeMode.Environment);
        if (File.Exists(Path.Combine(exeDirectory, PortableMarker))) return (Path.Combine(exeDirectory, "data"), HomeMode.Portable);
        return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoorVox"), HomeMode.Default);
    }

    public void Save() => FileUtil.WriteAllTextAtomic(SettingsPath, Json.Serialize(Settings));

    public ResolvedPath Resolve(StorageKind kind)
    {
        var configured = kind switch
        {
            StorageKind.Models => Settings.Paths.Models,
            StorageKind.Cache => Settings.Paths.Cache,
            StorageKind.Temp => Settings.Paths.Temp,
            StorageKind.Projects => Settings.Paths.Projects,
            StorageKind.Exports => Settings.Paths.Exports,
            StorageKind.WebView => Settings.Paths.WebView,
            StorageKind.Recordings => Settings.Paths.Recordings,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!string.IsNullOrWhiteSpace(configured))
            return new ResolvedPath(kind, Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured)), "configured");
        return new ResolvedPath(kind, DefaultPath(kind), Mode == HomeMode.Default ? "default" : $"default ({Mode.ToString().ToLowerInvariant()} home)");
    }

    public string ModelsDir => Resolve(StorageKind.Models).Path;
    public string CacheDir => Resolve(StorageKind.Cache).Path;
    public string TempDir => Resolve(StorageKind.Temp).Path;
    public string LogsDir => Path.Combine(TempDir, "logs");

    string DefaultPath(StorageKind kind)
    {
        string name = kind == StorageKind.WebView ? "WebView2" : kind.ToString();
        if (Mode != HomeMode.Default) return Path.Combine(Home, name);
        return kind switch
        {
            StorageKind.Projects or StorageKind.Exports or StorageKind.Recordings =>
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NoorVox", name),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoorVox", name),
        };
    }

    /// <summary>Sets a path (validated writable, directory created) and saves settings. <c>null</c> resets to default.</summary>
    public ResolvedPath SetPath(StorageKind kind, string? path)
    {
        string? value = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            value = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!FileUtil.TryProbeWritable(value, out var err))
                throw new NoorVoxException(ErrorCodes.PermissionDenied, ExitCode.IoError, $"Cannot write to {value}: {err}", new { kind, path = value });
        }
        var p = Settings.Paths;
        switch (kind)
        {
            case StorageKind.Models: p.Models = value; break;
            case StorageKind.Cache: p.Cache = value; break;
            case StorageKind.Temp: p.Temp = value; break;
            case StorageKind.Projects: p.Projects = value; break;
            case StorageKind.Exports: p.Exports = value; break;
            case StorageKind.WebView: p.WebView = value; break;
            case StorageKind.Recordings: p.Recordings = value; break;
        }
        Save();
        return Resolve(kind);
    }

    /// <summary>Read-only report: never creates directories.</summary>
    public IReadOnlyList<object> Report() =>
        Enum.GetValues<StorageKind>().Select(k =>
        {
            var r = Resolve(k);
            bool exists = Directory.Exists(r.Path);
            return (object)new
            {
                kind = k.ToString().ToLowerInvariant(),
                path = r.Path,
                source = r.Source,
                drive = System.IO.Path.GetPathRoot(r.Path),
                exists,
                freeBytes = FileUtil.FreeBytes(r.Path),
            };
        }).ToList();
}

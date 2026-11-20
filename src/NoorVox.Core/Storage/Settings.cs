namespace NoorVox.Core.Storage;

/// <summary>Persisted user settings (<c>settings.json</c> in the NoorVox home). Schema documented in docs/STORAGE_AND_PRIVACY.md.</summary>
public sealed class NoorVoxSettings
{
    public int SchemaVersion { get; set; } = 1;
    public StoragePathSettings Paths { get; set; } = new();
    /// <summary>Model reference (<c>id</c> or <c>id@version</c>) used when a command doesn't name one.</summary>
    public string? DefaultModel { get; set; }
    public string? DefaultVoice { get; set; }
    public WorkerSettings Workers { get; set; } = new();
}

/// <summary>Each storage class is independently configurable. <c>null</c> = use the default for the current home mode.</summary>
public sealed class StoragePathSettings
{
    public string? Models { get; set; }
    public string? Cache { get; set; }
    public string? Temp { get; set; }
    public string? Projects { get; set; }
    public string? Exports { get; set; }
    public string? WebView { get; set; }
}

public sealed class WorkerSettings
{
    /// <summary>Workers started for later reuse exit after this many idle seconds (0 = never). Safety net against forgotten workers.</summary>
    public int IdleTimeoutSeconds { get; set; } = 900;
    /// <summary>GUI policy: stop managed workers when the studio closes (spec §7.4 default).</summary>
    public bool StopOnExit { get; set; } = true;
}

public enum StorageKind { Models, Cache, Temp, Projects, Exports, WebView }

public enum HomeMode
{
    /// <summary>%APPDATA%\NoorVox — the normal installed experience.</summary>
    Default,
    /// <summary><c>NOORVOX_HOME</c> environment variable (tests, agents, custom setups).</summary>
    Environment,
    /// <summary>A <c>noorvox.portable</c> file next to the executable: everything lives in <c>.\data</c>.</summary>
    Portable,
}

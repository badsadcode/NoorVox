using System.Reflection;

namespace NoorVox.Core;

/// <summary>Version facts shared by every NoorVox entry point.</summary>
public static class NoorVoxInfo
{
    /// <summary>Version of the CLI JSON envelope and IPC contract. Bump on breaking changes.</summary>
    public const int SchemaVersion = 1;

    public static string Version { get; } =
        typeof(NoorVoxInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";
}

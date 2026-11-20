namespace NoorVox.Core.Storage;

public sealed record AuditEntry(string Kind, string Path, string Source, string? Drive, bool Exists, long? Bytes, int? Files, long? FreeBytes, bool OnSystemDrive);

/// <summary>NoorVox data found somewhere that is not part of the current configuration.</summary>
public sealed record StrayLocation(string Path, long Bytes, int Files, string Note);

public sealed record StorageAuditReport(
    string Home, string HomeMode, string SettingsFile, string SystemDrive,
    IReadOnlyList<AuditEntry> Paths, IReadOnlyList<StrayLocation> Strays, long TotalBytes, IReadOnlyList<string> Notes);

/// <summary>
/// Storage Audit (spec §8.2): every configured and resolved location with size, file count and free space, plus NoorVox
/// data found in default locations that the current configuration does not use (e.g. after a first run with defaults,
/// or a different launcher seeing a different AppData). Read-only: it never deletes or moves anything.
/// </summary>
public static class StorageAudit
{
    public static StorageAuditReport Run(StorageContext s, bool measure = true) => Run(s, measure, Candidates());

    /// <summary>Testable core: <paramref name="candidates"/> are places where stray NoorVox data may exist.</summary>
    internal static StorageAuditReport Run(StorageContext s, bool measure, IEnumerable<(string Path, string Note)> candidates)
    {
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var entries = new List<AuditEntry>();
        foreach (var kind in Enum.GetValues<StorageKind>())
        {
            var r = s.Resolve(kind);
            var (bytes, files) = measure && Directory.Exists(r.Path) ? Measure(r.Path) : (default(long?), default(int?));
            var drive = Path.GetPathRoot(r.Path);
            entries.Add(new AuditEntry(kind.ToString().ToLowerInvariant(), r.Path, r.Source, drive, Directory.Exists(r.Path), bytes, files,
                Util.FileUtil.FreeBytes(r.Path), string.Equals(drive, systemDrive, StringComparison.OrdinalIgnoreCase)));
        }
        var homeEntry = new AuditEntry("home", s.Home, s.Mode.ToString().ToLowerInvariant(), Path.GetPathRoot(s.Home), Directory.Exists(s.Home),
            measure && Directory.Exists(s.Home) ? Measure(s.Home).Bytes : null, null, Util.FileUtil.FreeBytes(s.Home),
            string.Equals(Path.GetPathRoot(s.Home), systemDrive, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, homeEntry);

        var used = entries.Select(e => Normalize(e.Path)).ToList();
        var strays = new List<StrayLocation>();
        foreach (var (candidate, note) in candidates)
        {
            var full = Normalize(candidate);
            if (!Directory.Exists(full)) continue;
            // Skip locations the configuration uses (or that contain / are inside a used location).
            if (used.Any(u => u.Equals(full, StringComparison.OrdinalIgnoreCase)
                || u.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(u + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            var (b, f) = Measure(full);
            if (f > 0) strays.Add(new StrayLocation(full, b ?? 0, f ?? 0, note));
        }

        var notes = new List<string>
        {
            "C: is allowed. Paths on the system drive are listed so you can decide, not because they're wrong.",
            "Windows and the Edge WebView2 Runtime may still write small OS-managed files (crash reports, runtime updates) outside these folders; NoorVox doesn't track those.",
        };
        if (strays.Count > 0)
            notes.Add("Stray locations contain NoorVox data the current settings don't use (for example from a first run with default paths). They're safe to delete if you don't need them; NoorVox never deletes them itself.");
        return new StorageAuditReport(s.Home, s.Mode.ToString().ToLowerInvariant(), s.SettingsPath, systemDrive, entries, strays,
            entries.Skip(1).Where(e => e.Bytes is not null).Sum(e => e.Bytes!.Value), notes);
    }

    static IEnumerable<(string Path, string Note)> Candidates()
    {
        string App(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        yield return (Path.Combine(App(Environment.SpecialFolder.LocalApplicationData), "NoorVox"), "default location for models, cache, temp audio and interface data");
        yield return (Path.Combine(App(Environment.SpecialFolder.ApplicationData), "NoorVox"), "default settings home");
        yield return (Path.Combine(App(Environment.SpecialFolder.MyDocuments), "NoorVox"), "default location for projects and exports");
        yield return (Path.Combine(AppContext.BaseDirectory, "data"), "portable-mode home next to the executable");
    }

    static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);

    static (long? Bytes, int? Files) Measure(string dir)
    {
        long bytes = 0;
        int files = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                bytes += f.Length;
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (bytes, files);
    }
}

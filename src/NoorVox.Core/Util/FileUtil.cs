using System.Security.Cryptography;

namespace NoorVox.Core.Util;

public static class FileUtil
{
    /// <summary>Writes text via a temp file in the same directory, then replaces the target (no torn files on crash).</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    public static string Sha256File(string path, Action<long>? onBytes = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            total += read;
            onBytes?.Invoke(total);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static string Sha256Text(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Directory.Move only works within a volume; fall back to copy + delete across drives.</summary>
    public static void MoveDirectory(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }
        CopyDirectory(source, destination);
        Directory.Delete(source, recursive: true);
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    /// <summary>True when a file can be created in <paramref name="dir"/> (creates the directory).</summary>
    public static bool TryProbeWritable(string dir, out string? error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".noorvox-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

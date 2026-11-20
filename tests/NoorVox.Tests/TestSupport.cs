using System.Diagnostics;
using NoorVox.Core.Storage;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // NOORVOX_HOME is process-global

namespace NoorVox.Tests;

/// <summary>An isolated NoorVox home (settings, storage, worker registry) for one test.</summary>
public sealed class TestHome : IDisposable
{
    readonly string? _previous = Environment.GetEnvironmentVariable(StorageContext.HomeEnvVar);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "noorvox-tests", Guid.NewGuid().ToString("N")[..10]);

    public TestHome()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(StorageContext.HomeEnvVar, Root);
    }

    public StorageContext Storage() => StorageContext.Load();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageContext.HomeEnvVar, _previous);
        for (int i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}

public static class TestPaths
{
    public static string BinDir => AppContext.BaseDirectory;
    public static string WorkerExe => Path.Combine(BinDir, "NoorVox.Worker.exe");
    public static string CliExe => Path.Combine(BinDir, "noorvox.exe");

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NoorVox.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
        }
    }

    /// <summary>
    /// Folder containing an installed models root (e.g. E:\AI\NoorVox\Models). Synthesis tests skip without it,
    /// because the model is ~400 MB and not part of the repository.
    /// </summary>
    public static string? ModelsDir =>
        Environment.GetEnvironmentVariable("NOORVOX_TEST_MODELS_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}

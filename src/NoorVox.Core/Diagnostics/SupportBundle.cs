using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Core.Diagnostics;

/// <summary>
/// A zip for troubleshooting (spec §13.3) that contains facts about the installation and recent logs, but never scripts,
/// audio, takes or project contents. What's inside is listed in the bundle's README.txt.
/// </summary>
public static class SupportBundle
{
    const long MaxLogBytes = 1 << 20; // last 1 MB of each log

    public static async Task<string> CreateAsync(StorageContext s, WorkerSupervisor supervisor, string zipPath, string producedBy, CancellationToken ct)
    {
        zipPath = Path.GetFullPath(zipPath);
        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipPath += ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var tmp = zipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        var workers = await supervisor.ListAsync(ct);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add("environment.json", Json.Serialize(new
            {
                noorvox = NoorVoxInfo.Version,
                producedBy,
                createdAt = DateTimeOffset.Now,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                processorCount = Environment.ProcessorCount,
                memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                executable = Environment.ProcessPath,
            }));
            // Settings: paths and preferences. Recent project paths are reduced to their folder names.
            var settings = Json.Deserialize<NoorVoxSettings>(Json.Serialize(s.Settings))!;
            settings.Studio.RecentProjects = settings.Studio.RecentProjects.Select(p => $"…\\{Path.GetFileName(p.TrimEnd('\\', '/'))}").ToList();
            Add("settings.json", Json.Serialize(new { home = s.Home, mode = s.Mode.ToString(), settingsFile = s.SettingsPath, settings }));
            Add("storage-audit.json", Json.Serialize(StorageAudit.Run(s)));
            Add("models.json", Json.Serialize(new ModelRegistry(s).List().Select(m => new
            {
                m.Reference, m.Manifest.DisplayName, m.Manifest.License, m.Manifest.Origin, m.Manifest.InstalledAt, m.ModelSha256, m.Directory,
                voices = m.Voices.Count, voicePacks = m.VoicePacks.Select(p => p.Id),
                quickVerify = ModelRegistry.Verify(m.Directory, m.Manifest.Files.Concat(m.VoicePacks.SelectMany(p => p.Files)), full: false),
            })));
            Add("workers.json", Json.Serialize(workers));
            // Engine packs: id, state and what the engine detected at install (versions, CUDA, GPU name). Voice profiles are not
            // included: they hold a person's voice recording and transcript.
            Add("engines.json", Json.Serialize(new Engines.EngineRegistry(s).List(ModelCatalog.LoadEmbedded())));

            if (Directory.Exists(s.LogsDir))
                foreach (var log in Directory.EnumerateFiles(s.LogsDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
                    Add($"logs/{Path.GetFileName(log)}", Tail(log));

            Add("README.txt", $"""
                NoorVox support bundle ({NoorVoxInfo.Version}, {DateTimeOffset.Now:yyyy-MM-dd HH:mm})

                Included: environment.json (OS, .NET, CPU count, memory), settings.json (storage paths and preferences;
                recent projects reduced to folder names), storage-audit.json (folders, sizes, free space), models.json
                (installed models, hashes, quick verification), workers.json (running NoorVox workers), engines.json (engine packs and
                the GPU they detected), logs/ (last 1 MB
                of each NoorVox log: studio and workers).

                NOT included: scripts, segment text, audio, takes, exports, project files, model files.
                Note: logs and paths can contain folder names from your PC. Review before sharing.
                """);
        }
        File.Move(tmp, zipPath, overwrite: true);
        return zipPath;
    }

    static string Tail(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxLogBytes) fs.Seek(-MaxLogBytes, SeekOrigin.End);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }
}

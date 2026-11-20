using System.IO.Compression;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Diagnostics;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Tests;

public class StorageAuditTests
{
    [Fact]
    public void Reports_sizes_and_finds_stray_noorvox_data_but_ignores_configured_folders()
    {
        using var home = new TestHome();
        var s = home.Storage();
        var models = Path.Combine(home.Root, "MyModels");
        s.SetPath(StorageKind.Models, models);
        File.WriteAllBytes(Path.Combine(models, "weights.bin"), new byte[4096]);
        var stray = Path.Combine(Path.GetTempPath(), "noorvox-tests", "stray-" + Guid.NewGuid().ToString("N")[..6]); // outside the home
        Directory.CreateDirectory(Path.Combine(stray, "Models"));
        File.WriteAllBytes(Path.Combine(stray, "Models", "kokoro.onnx"), new byte[10_000]);
        var empty = Path.Combine(home.Root, "EmptyCandidate");
        Directory.CreateDirectory(empty);

        var r = StorageAudit.Run(home.Storage(), true, [(stray, "default location"), (models, "configured"), (empty, "empty"), (Path.Combine(home.Root, "nope"), "missing")]);

        var m = r.Paths.Single(p => p.Kind == "models");
        Assert.Equal(4096, m.Bytes);
        Assert.Equal(1, m.Files);
        Assert.Equal("configured", m.Source);
        var found = Assert.Single(r.Strays);               // not the configured folder, not empty or missing ones
        Assert.Equal(stray, found.Path);
        Assert.Equal(10_000, found.Bytes);
        Assert.True(File.Exists(Path.Combine(stray, "Models", "kokoro.onnx"))); // audit never deletes
        Assert.Contains(r.Notes, n => n.Contains("C: is allowed"));
        Directory.Delete(stray, true);
    }

    [Fact]
    public async Task Support_bundle_has_facts_and_logs_but_no_project_content()
    {
        using var home = new TestHome();
        var s = home.Storage();
        s.Settings.Studio.RecentProjects.Add(@"E:\Secret Stuff\My Diary Audiobook");
        s.Save();
        s = home.Storage();
        Directory.CreateDirectory(s.LogsDir);
        File.WriteAllText(Path.Combine(s.LogsDir, "studio.log"), "studio started");
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");

        var zip = await SupportBundle.CreateAsync(s, sup, Path.Combine(home.Root, "bundle"), "test", CancellationToken.None);

        Assert.EndsWith(".zip", zip);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(["environment.json", "settings.json", "storage-audit.json", "models.json", "workers.json", "engines.json", "logs/studio.log", "README.txt"], names);
        using var reader = new StreamReader(archive.GetEntry("settings.json")!.Open());
        var settings = reader.ReadToEnd();
        Assert.DoesNotContain("Secret Stuff", settings);          // recent project paths reduced to folder names
        Assert.Contains("My Diary Audiobook", settings);
    }
}

public class ProjectManagementTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "pm-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    static InstalledModel FakeModel() => new(
        new PackageManifest { Id = "kokoro-82m", Version = "1.0", Backend = "kokoro-onnx", DefaultVoice = "af_heart", Files = [new("kokoro.onnx", 1, new string('a', 64))] },
        "X:\\fake", [new PackageManifest { Kind = "voicePack", Id = "p", Files = [new("voices/af_heart.npy", 1, "x")] }]);

    static Task<SynthesisResult> Synth(string text, string voice, double speed, string? style, string output, CancellationToken ct)
    {
        WavWriter.WritePcm16(output, new byte[4800], 24_000);
        return Task.FromResult(new SynthesisResult(output, 24_000, 1, 16, 2400, 100, 1));
    }

    [Fact]
    public void Journal_entries_after_the_last_snapshot_survive_a_hard_crash()
    {
        Directory.CreateDirectory(_root);
        var p = ProjectStore.Create(Path.Combine(_root, "Crash"), "Crash", "audiobook", "af_heart");
        var md = Path.Combine(_root, "c.md");
        File.WriteAllText(md, "# C\n\nA.\n\nB.\n\nC.\n");
        ScriptImporter.ImportInto(p, md);
        var runner = new JobRunner(p, FakeModel());
        var job = runner.Create(new JobScope(), "missing", false);
        // Snapshot says "running, nothing done"; the journal has two finished items; the process is gone (no lock).
        job.State = JobStates.Running;
        File.WriteAllText(Path.Combine(p.JobsDir, $"{job.JobId}.json"), Json.Serialize(job, indented: false));
        File.WriteAllLines(Path.Combine(p.JobsDir, $"{job.JobId}.journal"), [
            Json.Serialize(new JobItem { SegmentId = job.Items[0].SegmentId, State = ItemStates.Generated, TakeId = "t1" }, indented: false),
            Json.Serialize(new JobItem { SegmentId = job.Items[1].SegmentId, State = ItemStates.Generated, TakeId = "t2" }, indented: false),
            "{\"segmentId\":\"torn-li",   // half-written last line from the crash
        ]);

        var recovered = new JobRunner(ProjectStore.Open(p.Root), FakeModel()).Load(job.JobId);

        Assert.Equal(JobStates.Interrupted, recovered.State);
        Assert.Equal(2, recovered.Done);
        Assert.Equal("t2", recovered.Items[1].TakeId);
        Assert.Equal(ItemStates.Queued, recovered.Items[2].State);
    }

    [Fact]
    public async Task Duplicate_keeps_approvals_gets_new_id_and_leaves_history_behind()
    {
        Directory.CreateDirectory(_root);
        var p = ProjectStore.Create(Path.Combine(_root, "Original"), "Original", "audiobook", "af_heart");
        var md = Path.Combine(_root, "b.md");
        File.WriteAllText(md, "# Chapter\n\nOne.\n\nTwo.\n");
        ScriptImporter.ImportInto(p, md);
        var model = FakeModel();
        var runner = new JobRunner(p, model);
        await runner.RunAsync(runner.Create(new JobScope(), "missing", true), Synth, null, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(p.Root, "exports", "big-export.wav"), "x");

        var copy = p.Duplicate(Path.Combine(_root, "Copy"));

        Assert.NotEqual(p.Project.ProjectId, copy.Project.ProjectId);
        Assert.Equal("Original (copy)", copy.Project.Title);
        Assert.Equal(2, ProjectOverview.Build(copy, model).Counts[SegmentStatus.Approved]); // approvals still point at copied takes
        Assert.Empty(Directory.EnumerateFiles(copy.JobsDir));
        Assert.False(File.Exists(Path.Combine(copy.Root, "exports", "big-export.wav")));
        Assert.True(File.GetAttributes(Directory.GetFiles(Path.Combine(copy.Root, "references"))[0]).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("Original", ProjectStore.Open(p.Root).Project.Title);                // original untouched

        Assert.Throws<NoorVoxException>(() => p.Duplicate(Path.Combine(p.Root, "inside")));
        Assert.Throws<NoorVoxException>(() => p.Duplicate(copy.Root));                   // not empty

        copy.Rename("  Lighthouse, take two ");
        Assert.Equal("Lighthouse, take two", ProjectStore.Open(copy.Root).Project.Title);
        Assert.Throws<NoorVoxException>(() => copy.Rename(" "));
    }
}

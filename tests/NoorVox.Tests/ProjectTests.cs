using System.Security.Cryptography;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;

namespace NoorVox.Tests;

/// <summary>Project format, import, takes/approvals and interruption recovery — with a fake synthesizer (fast, deterministic).</summary>
public class ProjectTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "proj-" + Guid.NewGuid().ToString("N")[..8]);

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
        "X:\\fake",
        [new PackageManifest { Kind = "voicePack", Id = "pack", Files = [new("voices/af_heart.npy", 1, "x"), new("voices/am_adam.npy", 1, "x"), new("voices/bm_george.npy", 1, "x")] }]);

    /// <summary>Writes a short real WAV; optionally cancels on the Nth call (simulating Ctrl+C / crash mid-job).</summary>
    sealed class FakeSynth(int cancelOnCall = -1)
    {
        public int Calls;
        public List<string> Texts { get; } = [];
        public Task<SynthesisResult> Invoke(string text, string voice, double speed, string? style, string output, CancellationToken ct)
        {
            Calls++;
            if (Calls == cancelOnCall) throw new OperationCanceledException();
            Texts.Add(text);
            WavWriter.WritePcm16(output, new byte[24_000 * 2 * Math.Max(1, text.Length / 20) / 4], 24_000);
            var info = WavInfo.Read(output);
            return Task.FromResult(new SynthesisResult(output, info.SampleRate, 1, 16, info.Samples, info.DurationMs, 5));
        }
    }

    const string Markdown = """
        # Episode 1 – The Terrible Idea

        ## The First Image

        It started, as so many terrible ideas do, with a single image.

        <!-- visual: the first triangle screenshot -->
        A window, a **Vulkan** device, and a [triangle](https://example.com).

        ## Closing

        - The first spark of NoorEngine.
        - To be continued.

        # Episode 2 – Let There Be Light

        Light arrived later.
        """;

    ProjectStore NewProject(string kind = "documentary")
    {
        var p = ProjectStore.Create(Path.Combine(_root, kind), "Test " + kind, kind, voiceId: "bm_george");
        return p;
    }

    string WriteFile(string name, string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Markdown_import_builds_sections_scenes_segments_with_stable_ids_and_notes()
    {
        var p = NewProject();
        var (sections, reference) = ScriptImporter.ImportInto(p, WriteFile("ep.md", Markdown));

        Assert.Equal(2, sections.Count);
        Assert.Equal(["ep01", "ep02"], p.Project.Sections.Select(s => s.SectionId));
        var ep1 = p.LoadSection("ep01");
        Assert.Equal("Episode 1 – The Terrible Idea", ep1.Title);
        Assert.Equal(["The First Image", "Closing"], ep1.Scenes.Select(s => s.Title));
        var segs = ep1.Scenes[0].Segments;
        Assert.Equal("ep01.s01.p001", segs[0].SegmentId);
        Assert.Equal("A window, a Vulkan device, and a triangle.", segs[1].Text); // markdown stripped, no words invented
        Assert.Equal("visual: the first triangle screenshot", segs[1].Notes);    // notes are never spoken
        Assert.Equal(["The first spark of NoorEngine.", "To be continued."], ep1.Scenes[1].Segments.Select(s => s.Text));
        Assert.True(File.GetAttributes(reference).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(ProjectOverview.Validate(ProjectStore.Open(p.Root), FakeModel()));
    }

    [Fact]
    public void Dialogue_csv_keeps_line_ids_creates_characters_and_rejects_duplicates()
    {
        var p = NewProject("dialogue");
        var csv = WriteFile("lines.csv", "line_id,character,text,set\nnpc_vendor_greeting_001,Vendor,\"Welcome, traveller!\",Market\nnpc_vendor_bye_001,Vendor,Safe travels.,Market\nhero_reply_001,Alden,\"I'll take the \"\"blue\"\" one.\",Market\n");
        ScriptImporter.ImportInto(p, csv);

        var lines = p.LoadSection(p.Project.Sections[0].SectionId).Scenes[0].Segments;
        Assert.Equal(["npc_vendor_greeting_001", "npc_vendor_bye_001", "hero_reply_001"], lines.Select(l => l.SegmentId));
        Assert.Equal("I'll take the \"blue\" one.", lines[2].Text);
        Assert.Equal(["vendor", "alden"], p.Project.Speakers.Select(s => s.SpeakerId));
        Assert.Equal("Market", p.Project.Sections[0].Title);

        var dup = WriteFile("dup.csv", "line_id,character,text\na_1,X,One\na_1,X,Two\n");
        var ex = Assert.Throws<NoorVoxException>(() => ScriptImporter.ImportInto(p, dup));
        Assert.Equal(ErrorCodes.InvalidInput, ex.Code);
        // the same ids again (already in the project) are also refused
        Assert.Throws<NoorVoxException>(() => ScriptImporter.ImportInto(p, csv));
    }

    [Fact]
    public async Task Generation_reuses_matching_takes_and_edits_never_touch_the_approved_take()
    {
        var p = NewProject();
        ScriptImporter.ImportInto(p, WriteFile("ep.md", Markdown));
        var model = FakeModel();
        var runner = new JobRunner(p, model);
        var synth = new FakeSynth();

        var job = await runner.RunAsync(runner.Create(new JobScope { SectionId = "ep01" }, "missing", autoApprove: true), synth.Invoke, null, null, CancellationToken.None);
        Assert.Equal(JobStates.Completed, job.State);
        Assert.Equal(4, synth.Calls);
        var overview = ProjectOverview.Build(p, model);
        Assert.Equal(4, overview.Sections[0].Counts[SegmentStatus.Approved]);
        Assert.Equal(1, overview.Sections[1].Counts[SegmentStatus.Missing]);

        // Same settings again: nothing regenerated.
        var again = await runner.RunAsync(runner.Create(new JobScope { SectionId = "ep01" }, "missing", true), synth.Invoke, null, null, CancellationToken.None);
        Assert.Equal(4, synth.Calls);
        Assert.All(again.Items, i => Assert.Equal(ItemStates.Reused, i.State));

        // Edit one segment: it becomes stale; regenerating makes a NEW take and keeps the approval and its file intact.
        var segId = "ep01.s01.p001";
        var approvedId = p.Find(segId).Segment.ApprovedTakeId!;
        var approvedWav = p.SafePath(p.Takes(segId).Single().File);
        var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(approvedWav)));
        p.UpdateSegment(segId, s => s.Text = "It began, as terrible ideas do, with one image.");
        Assert.Equal(SegmentStatus.Stale, ProjectOverview.Build(p, model).Sections[0].Scenes[0].Segments[0].Status);

        await runner.RunAsync(runner.Create(new JobScope { SegmentIds = [segId] }, "missing", true), synth.Invoke, null, null, CancellationToken.None);
        Assert.Equal(5, synth.Calls);
        Assert.Equal(2, p.Takes(segId).Count);
        Assert.Equal(approvedId, p.Find(segId).Segment.ApprovedTakeId);          // auto-approve never replaces an approval
        Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(approvedWav))));
        Assert.Equal(SegmentStatus.Stale, ProjectOverview.Build(p, model).Sections[0].Scenes[0].Segments[0].Status);

        // Approving the new take is an explicit choice.
        p.Approve(segId, p.Takes(segId).Last().TakeId);
        Assert.Equal(SegmentStatus.Approved, ProjectOverview.Build(p, model).Sections[0].Scenes[0].Segments[0].Status);
    }

    [Fact]
    public async Task Interrupted_job_keeps_finished_takes_and_resumes_where_it_stopped()
    {
        var p = NewProject("audiobook");
        ScriptImporter.ImportInto(p, WriteFile("book.md", "# Chapter One\n\nFirst.\n\nSecond.\n\nThird.\n\nFourth.\n\nFifth.\n"));
        var model = FakeModel();
        var crashing = new FakeSynth(cancelOnCall: 3);
        var job = new JobRunner(p, model).Create(new JobScope(), "missing", false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JobRunner(p, model).RunAsync(job, crashing.Invoke, null, null, CancellationToken.None));

        // "Restart": fresh objects read only what is on disk.
        var reopened = ProjectStore.Open(p.Root);
        var saved = new JobRunner(reopened, model).Load(job.JobId);
        Assert.Equal(JobStates.Interrupted, saved.State);
        Assert.Equal(2, saved.Done);
        Assert.Equal(ItemStates.Interrupted, saved.Items[2].State);
        Assert.Empty(Directory.EnumerateFiles(reopened.TempDir)); // no partial audio reported as a take
        Assert.Equal(2, reopened.AllSegments().Count(l => reopened.Takes(l.Segment.SegmentId).Count > 0));

        var resumed = new FakeSynth();
        var done = await new JobRunner(reopened, model).RunAsync(saved, resumed.Invoke, null, null, CancellationToken.None);
        Assert.Equal(JobStates.Completed, done.State);
        Assert.Equal(3, resumed.Calls);                 // only the unfinished three
        Assert.Equal(["Third.", "Fourth.", "Fifth."], resumed.Texts);
        Assert.Equal(5, done.Done);
    }

    [Fact]
    public void A_job_left_running_by_a_dead_process_is_reported_interrupted()
    {
        var p = NewProject();
        ScriptImporter.ImportInto(p, WriteFile("ep.md", Markdown));
        var runner = new JobRunner(p, FakeModel());
        var job = runner.Create(new JobScope(), "missing", false);
        job.State = JobStates.Running; // as if the process died mid-run
        job.Items[0].State = ItemStates.Running;
        File.WriteAllText(Path.Combine(p.JobsDir, $"{job.JobId}.json"), Json.Serialize(job));

        var listed = runner.List().Single();
        Assert.Equal(JobStates.Interrupted, listed.State);
        Assert.Equal(ItemStates.Interrupted, listed.Items[0].State);
    }

    [Fact]
    public async Task Stop_after_current_pauses_cleanly()
    {
        var p = NewProject();
        ScriptImporter.ImportInto(p, WriteFile("ep.md", Markdown));
        var synth = new FakeSynth();
        var job = await new JobRunner(p, FakeModel()).RunAsync(new JobRunner(p, FakeModel()).Create(new JobScope(), "missing", false),
            synth.Invoke, null, () => synth.Calls >= 2, CancellationToken.None);
        Assert.Equal(JobStates.Interrupted, job.State);
        Assert.Equal(2, job.Done);
    }

    [Fact]
    public async Task Clip_export_uses_line_ids_and_requires_approvals()
    {
        var p = NewProject("dialogue");
        ScriptImporter.ImportInto(p, WriteFile("lines.csv", "line_id,character,text\nnpc_vendor_greeting_001,Vendor,Welcome!\nnpc_vendor_bye_001,Vendor,Bye.\n"));
        var model = FakeModel();
        var runner = new JobRunner(p, model);
        await runner.RunAsync(runner.Create(new JobScope { SegmentIds = ["npc_vendor_greeting_001"] }, "missing", true), new FakeSynth().Invoke, null, null, CancellationToken.None);
        var outDir = Path.Combine(_root, "export");

        var ex = Assert.Throws<NoorVoxException>(() => ClipExporter.Export(p, model, null, outDir, allowMissing: false, overwrite: false));
        Assert.Contains("no approved take", ex.Message);

        var r = ClipExporter.Export(p, model, null, outDir, allowMissing: true, overwrite: false);
        Assert.Equal(["npc_vendor_greeting_001.wav"], r.Clips.Select(c => Path.GetFileName(c.File)));
        Assert.Equal(["npc_vendor_bye_001"], r.Missing);
        Assert.Equal("Vendor", r.Clips[0].Speaker);
        Assert.Equal(WavInfo.Read(r.Clips[0].File).DurationMs, r.Clips[0].DurationMs);
        Assert.True(File.Exists(r.ManifestPath));
        Assert.Throws<NoorVoxException>(() => ClipExporter.Export(p, model, null, outDir, true, overwrite: false)); // never overwrites silently
    }

    [Fact]
    public void Pronunciation_rules_change_spoken_text_and_cache_key()
    {
        var p = NewProject();
        ScriptImporter.ImportInto(p, WriteFile("ep.md", "# E\n\nNoorEngine draws a triangle.\n"));
        var model = FakeModel();
        var seg = p.AllSegments().First().Segment;
        var before = p.Effective(seg, model);
        File.WriteAllText(Path.Combine(p.Root, "voices", "pronunciation.json"),
            Json.Serialize(new PronunciationFile { Rules = [new PronunciationRule { Match = "NoorEngine", Replace = "Noor Engine" }] }));
        var after = p.Effective(seg, model);
        Assert.Equal("Noor Engine draws a triangle.", after.SpokenText);
        Assert.NotEqual(before.CacheKey, after.CacheKey);
        Assert.Equal("NoorEngine draws a triangle.", p.AllSegments().First().Segment.Text); // the script itself is untouched
    }

    [Fact]
    public void Creating_a_project_refuses_non_empty_folders()
    {
        var dir = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "something.txt"), "mine");
        var ex = Assert.Throws<NoorVoxException>(() => ProjectStore.Create(dir, "X", "narration"));
        Assert.Equal(ErrorCodes.OutputExists, ex.Code);
    }
}

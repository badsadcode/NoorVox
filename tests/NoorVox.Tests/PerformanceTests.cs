using System.Diagnostics;
using System.Text;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Export;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;

namespace NoorVox.Tests;

/// <summary>
/// NoorVox's own overhead on a large audiobook (2,000 paragraphs in 20 chapters) with an instant fake engine,
/// so synthesis time is excluded. Budgets are generous upper bounds; the measured numbers are printed and
/// recorded in docs/RELEASE_NOTES.md.
/// </summary>
public class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "perf-" + Guid.NewGuid().ToString("N")[..8]);

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

    static readonly byte[] Pcm = new byte[24_000]; // 0.5 s of silence-ish audio

    static Task<SynthesisResult> Instant(string text, string voice, double speed, string outputWav, CancellationToken ct)
    {
        WavWriter.WritePcm16(outputWav, Pcm, 24_000);
        return Task.FromResult(new SynthesisResult(outputWav, 24_000, 1, 16, 12_000, 500, 0));
    }

    [Fact]
    public async Task Large_audiobook_stays_responsive()
    {
        Directory.CreateDirectory(_root);
        var md = new StringBuilder();
        for (int c = 1; c <= 20; c++)
        {
            md.Append($"# Chapter {c}\n\n");
            for (int p = 1; p <= 100; p++) md.Append($"Paragraph {p} of chapter {c}. The sea argued with the wind, and the keeper lit the lamp anyway.\n\n");
        }
        var file = Path.Combine(_root, "book.md");
        File.WriteAllText(file, md.ToString());
        var store = ProjectStore.Create(Path.Combine(_root, "Book"), "Big Book", "audiobook", "af_heart");
        var model = FakeModel();
        var sw = Stopwatch.StartNew();
        string Lap() { var t = $"{sw.Elapsed.TotalSeconds:0.00} s"; sw.Restart(); return t; }

        ScriptImporter.ImportInto(store, file);
        var import = Lap();

        var overviewEmpty = ProjectOverview.Build(store, model);
        var ov1 = Lap();
        Assert.Equal(2000, overviewEmpty.SegmentCount);

        var runner = new JobRunner(store, model);
        var job = await runner.RunAsync(runner.Create(new JobScope(), "missing", autoApprove: true), Instant, null, null, CancellationToken.None);
        var generate = Lap();
        Assert.Equal(2000, job.Done);

        var full = ProjectOverview.Build(store, model);
        var ov2 = Lap();
        Assert.Equal(2000, full.Counts[SegmentStatus.Approved]);

        var again = await runner.RunAsync(runner.Create(new JobScope(), "missing", true), Instant, null, null, CancellationToken.None);
        var reuse = Lap();
        Assert.All(again.Items, i => Assert.Equal(ItemStates.Reused, i.State));

        store.UpdateSegment("ch10.s01.p050", s => s.Text = "An edited paragraph.");
        var edit = Lap();

        var asm = Assembler.AssembleSection(store, model, "ch01", Path.Combine(_root, "out"), new AssemblyOptions { SampleRate = 48_000 });
        var assemble = Lap();
        Assert.Equal(100, asm.Manifest.Segments.Count);

        output.WriteLine($"import 2000 paragraphs: {import}");
        output.WriteLine($"overview (no takes): {ov1}");
        output.WriteLine($"generate job, 2000 items, instant engine (NoorVox overhead only): {generate}");
        output.WriteLine($"overview (2000 approved takes): {ov2}");
        output.WriteLine($"re-run job, all reused: {reuse}");
        output.WriteLine($"edit one segment: {edit}");
        output.WriteLine($"assemble chapter (100 segments, 48 kHz): {assemble}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "noorvox-perf.txt"),
            string.Join("\n", import, ov1, generate, ov2, reuse, edit, assemble));
    }
}

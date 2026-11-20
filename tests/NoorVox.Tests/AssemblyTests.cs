using System.Text.Json;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Export;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;

namespace NoorVox.Tests;

public class ResamplerTests
{
    static PcmBuffer Tone(double hz, int rate, double seconds, float amp = 0.5f) =>
        new(Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray(), rate);

    static double Rms(float[] s, int skip) { double sum = 0; int n = 0; for (int i = skip; i < s.Length - skip; i++) { sum += s[i] * s[i]; n++; } return Math.Sqrt(sum / n); }
    static int ZeroCrossings(float[] s) { int z = 0; for (int i = 1; i < s.Length; i++) if (s[i - 1] < 0 != s[i] < 0) z++; return z; }

    [Fact]
    public void Upsampling_24k_to_48k_keeps_length_pitch_and_level()
    {
        var input = Tone(1000, 24_000, 1.0);
        var output = Resampler.Resample(input, 48_000);
        Assert.Equal(48_000, output.Samples.Length);
        Assert.Equal(input.DurationMs, output.DurationMs);
        Assert.InRange(ZeroCrossings(output.Samples), 1996, 2002);                    // still 1 kHz
        Assert.InRange(Rms(output.Samples, 200), 0.5 / Math.Sqrt(2) * 0.99, 0.5 / Math.Sqrt(2) * 1.01);
    }

    [Fact]
    public void Resampling_to_44_1k_has_exact_length_and_keeps_dc()
    {
        var dc = new PcmBuffer(Enumerable.Repeat(0.3f, 24_000).ToArray(), 24_000);
        var output = Resampler.Resample(dc, 44_100);
        Assert.Equal(44_100, output.Samples.Length);
        Assert.InRange(output.Samples[22_050], 0.299f, 0.301f);
    }

    [Fact]
    public void Downsampling_removes_content_above_the_new_nyquist()
    {
        var keep = Resampler.Resample(Tone(9_000, 48_000, 0.5), 24_000);
        var alias = Resampler.Resample(Tone(15_000, 48_000, 0.5), 24_000); // above 12 kHz: must not fold back as 9 kHz
        Assert.InRange(Rms(keep.Samples, 200), 0.5 / Math.Sqrt(2) * 0.97, 0.5 / Math.Sqrt(2) * 1.03);
        Assert.True(Rms(alias.Samples, 200) < 0.02, $"alias rms {Rms(alias.Samples, 200)}");
    }
}

/// <summary>Assembly timing, pauses, subtitles and policies with fake takes of exactly 0.5 s at amplitude 0.25.</summary>
public class AssemblyTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "asm-" + Guid.NewGuid().ToString("N")[..8]);

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
        "X:\\fake", [new PackageManifest { Kind = "voicePack", Id = "p", Files = [new("voices/af_heart.npy", 1, "x"), new("voices/bm_george.npy", 1, "x")] }]);

    static Task<SynthesisResult> HalfSecond(string text, string voice, double speed, string output, CancellationToken ct)
    {
        var pcm = new byte[12_000 * 2];
        for (int i = 0; i < 12_000; i++) { pcm[i * 2] = 0x00; pcm[i * 2 + 1] = 0x20; } // 8192 = 0.25 full scale
        WavWriter.WritePcm16(output, pcm, 24_000);
        return Task.FromResult(new SynthesisResult(output, 24_000, 1, 16, 12_000, 500, 1));
    }

    async Task<(ProjectStore Store, InstalledModel Model)> Project(string kind = "documentary", string? script = null, bool approve = true)
    {
        Directory.CreateDirectory(_root);
        var p = ProjectStore.Create(Path.Combine(_root, kind + "-" + Guid.NewGuid().ToString("N")[..4]), "Test", kind, "bm_george");
        var file = Path.Combine(_root, $"s{Guid.NewGuid():N}.{(kind == "dialogue" ? "csv" : "md")}");
        File.WriteAllText(file, script ?? "# Episode One\n\n## Opening\n\nFirst line.\n\nSecond line.\n\n## Closing\n\nThe end.\n");
        ScriptImporter.ImportInto(p, file);
        var model = FakeModel();
        var runner = new JobRunner(p, model);
        await runner.RunAsync(runner.Create(new JobScope(), "missing", approve), HalfSecond, null, null, CancellationToken.None);
        return (p, model);
    }

    string Out() => Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public async Task Timeline_uses_structured_pauses_and_sample_exact_offsets()
    {
        var (p, model) = await Project();
        var r = Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions());
        var m = r.Manifest;

        // edge 1500 + 500 + seg gap 350 + 500 + scene gap 900 + 500 + edge 1500 = 5750 ms
        Assert.Equal(5750, m.Audio.DurationMs);
        Assert.Equal(138_000, m.Audio.Samples);
        Assert.Equal([1500L, 2350L, 3750L], m.Segments.Select(s => s.StartMs));
        Assert.Equal([2000L, 2850L, 4250L], m.Segments.Select(s => s.EndMs));
        Assert.Equal(["ep01.s01", "ep01.s02"], m.Scenes.Select(s => s.SceneId));
        Assert.Equal(3750, m.Scenes[1].StartMs);

        var audio = PcmBuffer.ReadWav(r.AudioPath);
        Assert.Equal(0f, audio.Samples[24 * 1000]);                 // silence before the first segment
        Assert.InRange(audio.Samples[24 * 1600], 0.249f, 0.251f);   // inside segment 1
        Assert.Equal(0f, audio.Samples[24 * 2100]);                 // the 350 ms gap
        Assert.Equal(0f, audio.Samples[24 * 3500]);                 // the 900 ms scene gap
        Assert.Equal(m.Audio.Sha256, NoorVox.Core.Util.FileUtil.Sha256File(r.AudioPath));
    }

    [Fact]
    public async Task Segment_pause_overrides_win_over_defaults()
    {
        var (p, model) = await Project();
        p.UpdateSegment("ep01.s01.p002", s => s.PauseBeforeMs = 2000);
        p.UpdateSegment("ep01.s01.p002", s => s.PauseAfterMs = 100); // explicit, smaller than the 900 ms scene gap: explicit wins
        var m = Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions()).Manifest;
        Assert.Equal(1500 + 500 + 2000, m.Segments[1].StartMs);
        Assert.Equal(m.Segments[1].EndMs + 100, m.Segments[2].StartMs);
    }

    [Fact]
    public async Task Resampling_to_48k_keeps_times_and_scales_sample_offsets()
    {
        var (p, model) = await Project();
        var m = Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions { SampleRate = 48_000 }).Manifest;
        Assert.Equal(48_000, m.Audio.SampleRate);
        Assert.Equal(276_000, m.Audio.Samples);
        Assert.Equal(5750, m.Audio.DurationMs);
        Assert.Equal(1500, m.Segments[0].StartMs);
        Assert.Equal(72_000, m.Segments[0].StartSample);
        Assert.True(m.Processing.Resampled);
        Assert.Equal(24_000, m.Processing.SourceSampleRate);
    }

    [Fact]
    public async Task Peak_normalisation_is_export_only()
    {
        var (p, model) = await Project();
        var take = p.Takes("ep01.s01.p001").Single();
        var before = File.ReadAllBytes(p.SafePath(take.File));
        var r = Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions { NormalizePeak = true, PeakDbfs = -1 });
        Assert.InRange(PcmBuffer.ReadWav(r.AudioPath).Peak(), 0.885f, 0.895f);   // -1 dBFS
        Assert.InRange(r.Manifest.Processing.GainDb!.Value, 11.0, 11.1);        // 0.25 → 0.891
        Assert.Equal(before, File.ReadAllBytes(p.SafePath(take.File)));          // the take is untouched
    }

    [Fact]
    public async Task Subtitles_have_measured_cue_times()
    {
        var (p, model) = await Project();
        var r = Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions());
        var srt = File.ReadAllText(r.SrtPath!);
        Assert.StartsWith("1\r\n00:00:01,500 --> 00:00:02,000\r\nFirst line.\r\n\r\n2\r\n00:00:02,350 --> 00:00:02,850\r\n", srt);
        var vtt = File.ReadAllText(r.VttPath!);
        Assert.StartsWith("WEBVTT\n\nep01.s01.p001\n00:00:01.500 --> 00:00:02.000\nFirst line.\n", vtt);
        Assert.Equal(["a b c", "dd"], Subtitles.Wrap("a b c dd", 5));
    }

    [Fact]
    public async Task Dialogue_captions_carry_speakers()
    {
        var (p, model) = await Project("dialogue", "line_id,character,text\nv_hi_001,Vendor,Hello <there> & welcome.\nh_yes_001,Alden,Yes.\n");
        var r = Assembler.AssembleSection(p, model, p.Project.Sections[0].SectionId, Out(), new AssemblyOptions());
        Assert.Contains("VENDOR: Hello <there> & welcome.", File.ReadAllText(r.SrtPath!));
        Assert.Contains("<v Vendor>Hello &lt;there&gt; &amp; welcome.", File.ReadAllText(r.VttPath!));
        Assert.Equal("Alden", r.Manifest.Segments[1].Speaker);
    }

    [Fact]
    public async Task Missing_approvals_fail_unless_allowed_and_stale_is_reported()
    {
        var (p, model) = await Project(approve: false);
        p.Approve("ep01.s01.p001", p.Takes("ep01.s01.p001").Single().TakeId);
        var ex = Assert.Throws<NoorVoxException>(() => Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions()));
        Assert.Equal(ErrorCodes.InvalidInput, ex.Code);

        p.UpdateSegment("ep01.s01.p001", s => s.Text = "First line, edited.");
        var m = Assembler.AssembleSection(p, model, "ep01", Out(), new AssemblyOptions { AllowMissing = true }).Manifest;
        Assert.Single(m.Segments);
        Assert.Equal(["ep01.s01.p002", "ep01.s02.p001"], m.Missing);
        Assert.Equal(["ep01.s01.p001"], m.Stale);
        Assert.Equal(1500 + 500 + 1500, m.Audio.DurationMs);
    }

    [Fact]
    public async Task Never_overwrites_without_permission_and_manifest_matches_schema()
    {
        var (p, model) = await Project();
        var dir = Out();
        var r = Assembler.AssembleSection(p, model, "ep01", dir, new AssemblyOptions());
        var ex = Assert.Throws<NoorVoxException>(() => Assembler.AssembleSection(p, model, "ep01", dir, new AssemblyOptions()));
        Assert.Equal(ErrorCodes.OutputExists, ex.Code);
        Assembler.AssembleSection(p, model, "ep01", dir, new AssemblyOptions { Overwrite = true });

        // Every property the published JSON Schema requires is present in a real manifest.
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "schemas", "assembly-manifest.schema.json")));
        using var doc = JsonDocument.Parse(File.ReadAllText(r.ManifestPath));
        void Check(JsonElement schemaNode, JsonElement value, string at)
        {
            if (schemaNode.TryGetProperty("required", out var req))
                foreach (var name in req.EnumerateArray().Select(e => e.GetString()!))
                    Assert.True(value.TryGetProperty(name, out _), $"manifest misses {at}.{name}");
            if (schemaNode.TryGetProperty("properties", out var props))
                foreach (var prop in props.EnumerateObject())
                    if (value.TryGetProperty(prop.Name, out var child))
                    {
                        if (child.ValueKind == JsonValueKind.Object) Check(prop.Value, child, $"{at}.{prop.Name}");
                        if (child.ValueKind == JsonValueKind.Array && prop.Value.TryGetProperty("items", out var items) && child.GetArrayLength() > 0)
                            Check(items, child[0], $"{at}.{prop.Name}[0]");
                    }
        }
        Check(schema.RootElement, doc.RootElement, "$");
    }
}

using NoorVox.Backends.Python;
using NoorVox.Core;
using NoorVox.Core.Engines;
using NoorVox.Core.Models;
using NoorVox.Core.Narration;
using NoorVox.Core.Workers;

namespace NoorVox.Tests;

/// <summary>Chatterbox (ADR-0003): catalog, engine lock, honest controls. Real synthesis is opt-in.</summary>
public class ChatterboxTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "cbx-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    [Fact]
    public void Catalog_has_three_variants_with_only_the_controls_their_code_honours()
    {
        var catalog = ModelCatalog.LoadEmbedded();
        var en = catalog.FindModel("chatterbox-english")!;
        var ml = catalog.FindModel("chatterbox-multilingual")!;
        var turbo = catalog.FindModel("chatterbox-turbo")!;
        foreach (var m in new[] { en, ml, turbo })
        {
            Assert.Equal(("chatterbox", "chatterbox", "MIT"), (m.Backend, m.Engine, m.License));
            var c = m.Capabilities!;
            Assert.True(c.Cloning && c.Seed && c.RequiresGpu);
            Assert.False(c.Speed || c.Style || c.Design);
            Assert.Equal("perth", c.Watermark);
            Assert.Equal("default", Assert.Single(m.BuiltInVoices!).Id);
            Assert.All(m.Downloads, d => Assert.Contains("/resolve/", d.Url));
        }
        Assert.True(en.Capabilities!.Emotion && en.Capabilities.Pace);
        Assert.Equal(0.1, en.Capabilities.PaceMin);                       // 0.0 crashes chatterbox-tts 0.1.7
        Assert.False(turbo.Capabilities!.Emotion || turbo.Capabilities.Pace); // Turbo's code ignores both
        Assert.Empty(en.Capabilities.Languages);
        Assert.Equal(23, ml.Capabilities!.Languages.Count);
        Assert.All(ml.Capabilities.Languages, l => Assert.True(ChatterboxBackend.LanguageIds.ContainsKey(l), l));
        // Only the files each loader reads, not the whole repository.
        Assert.Contains(en.Files, f => f.Path == "t3_cfg.safetensors");
        Assert.DoesNotContain(en.Files, f => f.Path.Contains("mtl"));
        Assert.Contains(ml.Files, f => f.Path == "t3_mtl23ls_v2.safetensors");
        Assert.Contains(turbo.Files, f => f.Path == "s3gen_meanflow.safetensors");

        var engine = catalog.FindEngine(EngineIds.Chatterbox)!;
        Assert.Contains("perth.perth_net.perth_net_implicit.perth_watermarker", engine.VerifyImports!);
        Assert.Contains("https://download.pytorch.org/whl/cu124", engine.PipIndexUrls!);
    }

    [Fact]
    public void Engine_lock_pins_everything_and_keeps_pkg_resources()
    {
        using var stream = typeof(EngineInstaller).Assembly.GetManifestResourceStream("NoorVox.Core.Engines.chatterbox.lock")!;
        var lines = new StreamReader(stream).ReadToEnd().Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.All(lines, l => Assert.Matches(@"^[A-Za-z0-9_.\-]+==\S+( --hash=sha256:[0-9a-f]{64})+$", l));
        Assert.Contains(lines, l => l.StartsWith("chatterbox-tts==0.1.7 "));
        Assert.Contains(lines, l => l.StartsWith("torch==2.6.0+cu124 "));
        var setuptools = lines.Single(l => l.StartsWith("setuptools=="));
        Assert.True(Version.Parse(setuptools.Split("==")[1].Split(' ')[0]).Major < 81, "resemble-perth needs pkg_resources (setuptools < 81)");
    }

    [Fact]
    public void Emotion_and_pace_are_checked_against_the_model()
    {
        var dir = Path.Combine(_root, "chatterbox-english", "1.0");
        Directory.CreateDirectory(dir);
        var cat = ModelCatalog.LoadEmbedded().FindModel("chatterbox-english")!;
        File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(new PackageManifest
        {
            Id = cat.Id, Version = cat.Version, Backend = cat.Backend, Capabilities = cat.Capabilities, BuiltInVoices = cat.BuiltInVoices, Files = cat.Files,
        }));
        var m = ModelRegistry.ReadPackage(dir);
        NarrationService.CheckSliders(m, 1.2, 0.3);
        Assert.Equal(ErrorCodes.InvalidArguments, Assert.Throws<NoorVoxException>(() => NarrationService.CheckSliders(m, 3.0, null)).Code);
        Assert.Equal(ErrorCodes.InvalidArguments, Assert.Throws<NoorVoxException>(() => NarrationService.CheckSliders(m, null, 0.0)).Code);
        Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(m, 1.2, null, null, null)).Code);
        Assert.Equal("english", ChatterboxBackend.Variant(m));
        Assert.True(m.HasVoice("default"));
    }

    [Fact]
    public void Load_options_point_at_the_chatterbox_sidecar()
    {
        using var home = new TestHome();
        var s = home.Storage();
        var cat = ModelCatalog.LoadEmbedded().FindModel("chatterbox-turbo")!;
        var dir = Path.Combine(s.ModelsDir, cat.Id, cat.Version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(new PackageManifest
        {
            Id = cat.Id, Version = cat.Version, Backend = cat.Backend, Engine = cat.Engine, Capabilities = cat.Capabilities, Files = cat.Files,
        }));
        var engineDir = new EngineRegistry(s).EngineDir(EngineIds.Chatterbox, "1.0");
        Directory.CreateDirectory(engineDir);
        File.WriteAllText(Path.Combine(engineDir, EngineRegistry.ManifestName), Json.Serialize(new EngineManifest { Id = EngineIds.Chatterbox, Version = "1.0", Kind = "python" }));
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");
        var o = sup.LoadOptionsFor(ModelRegistry.ReadPackage(dir));
        Assert.EndsWith(Path.Combine("engines", "chatterbox", "noorvox_chatterbox.py"), o.SidecarScript);
        Assert.True(File.Exists(o.SidecarScript));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(o.SidecarScript)!, "..", "common", "noorvox_sidecar.py")));
    }

    /// <summary>Real Chatterbox synthesis: opt-in with NOORVOX_TEST_CHATTERBOX=1 (NVIDIA GPU, chatterbox engine and chatterbox-turbo in NOORVOX_TEST_MODELS_DIR).</summary>
    [Fact]
    public async Task Real_chatterbox_turbo_is_reproducible_and_watermarked()
    {
        if (TestPaths.ModelsDir is not { } models || Environment.GetEnvironmentVariable("NOORVOX_TEST_CHATTERBOX") != "1") return;
        using var home = new TestHome();
        var s = home.Storage();
        s.SetPath(NoorVox.Core.Storage.StorageKind.Models, models);
        var model = new ModelRegistry(s).Find("chatterbox-turbo");
        if (model is null || new EngineRegistry(s).Find(EngineIds.Chatterbox) is null) return;
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");
        var svc = new NarrationService(s, sup);
        async Task<NarrationResult> Say(string name) => await svc.SynthesizeAsync(new NarrationRequest
        {
            Text = "Testing, one two three.", VoiceId = "default", Model = model.Reference, Seed = 99,
            OutputPath = Path.Combine(_root, name), WorkerId = "cbx-test", KeepWorker = true,
        }, null, CancellationToken.None);
        try
        {
            var a = await Say("a.wav");
            var b = await Say("b.wav");
            Assert.Equal(("CUDA", "perth"), (a.Backend.ExecutionProvider, a.Watermark));
            Assert.Equal(File.ReadAllBytes(a.OutputPath).Skip(44), File.ReadAllBytes(b.OutputPath).Skip(44));
            var st = await sup.GetStatusAsync("cbx-test", CancellationToken.None);
            var pid = st.Resources!.Sidecar!.Pid;
            await sup.StopAsync("cbx-test", false, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(pid), TimeSpan.FromSeconds(15)), "the Python engine must not outlive its worker");
        }
        finally { await sup.StopAllAsync(true, TimeSpan.FromSeconds(10), CancellationToken.None); }
    }
}

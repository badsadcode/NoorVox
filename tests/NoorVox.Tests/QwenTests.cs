using System.IO.Compression;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Engines;
using NoorVox.Core.Models;
using NoorVox.Core.Narration;
using NoorVox.Core.Projects;
using NoorVox.Core.Workers;

namespace NoorVox.Tests;

/// <summary>Qwen3-TTS integration (ADR-0002): catalog, capabilities, voice profiles, engines. Real synthesis is opt-in.</summary>
public class QwenTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "qwen-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    static PackageManifest QwenManifest(string id, ModelCapabilities caps, List<BuiltInVoice>? voices = null) => new()
    {
        Id = id, Version = "1.0", Backend = "qwen3-tts", Engine = EngineIds.QwenTts, DisplayName = id, Capabilities = caps, BuiltInVoices = voices,
        DefaultVoice = voices?.FirstOrDefault()?.Id,
        Files = [new("config.json", 10, new string('c', 64)), new("model.safetensors", 1000, new string('b', 64)), new("speech_tokenizer/model.safetensors", 500, new string('d', 64))],
    };

    InstalledModel WriteModel(PackageManifest m)
    {
        var dir = Path.Combine(_root, "models", m.Id, m.Version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(m));
        return ModelRegistry.ReadPackage(dir);
    }

    static readonly ModelCapabilities CustomCaps = new() { Style = true, Seed = true, RequiresGpu = true, Languages = ["auto", "english"] };
    static readonly ModelCapabilities CloneCaps = new() { Cloning = true, Seed = true, RequiresGpu = true, Languages = ["auto", "english"] };

    string Wav(string name, double seconds)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        WavWriter.WritePcm16(path, new byte[(int)(seconds * 24000) * 2], 24000);
        return path;
    }

    [Fact]
    public void Catalog_qwen_models_declare_engine_capabilities_and_hashed_files()
    {
        var catalog = ModelCatalog.LoadEmbedded();
        var qwen = catalog.Models.Where(m => m.Backend == "qwen3-tts").ToList();
        Assert.Equal(5, qwen.Count); // CustomVoice and Base in 1.7B and 0.6B, plus 1.7B VoiceDesign
        foreach (var m in qwen)
        {
            Assert.Equal(EngineIds.QwenTts, m.Engine);
            Assert.NotNull(m.Capabilities);
            Assert.False(m.Capabilities!.Speed); // no fake speed slider
            Assert.True(m.Capabilities.Seed && m.Capabilities.RequiresGpu);
            Assert.Contains(m.Files, f => f.Path == "model.safetensors");
            Assert.All(m.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
            Assert.All(m.Downloads, d => Assert.Contains("/resolve/", d.Url)); // pinned revision URLs
            Assert.Equal(m.Files.Count, m.Downloads.Count);
        }
        Assert.True(catalog.FindModel("qwen3-tts-1.7b-customvoice")!.Capabilities!.Style);
        Assert.False(catalog.FindModel("qwen3-tts-0.6b-customvoice")!.Capabilities!.Style); // 0.6B ignores instructions
        Assert.True(catalog.FindModel("qwen3-tts-1.7b-base")!.Capabilities!.Cloning);
        Assert.Equal(9, catalog.FindModel("qwen3-tts-1.7b-customvoice")!.BuiltInVoices!.Count);

        // Kokoro: an explicit capability block equal to the built-in fallback used for old manifests.
        Assert.Equal(ModelCapabilities.Kokoro.MaxSpeed, catalog.FindModel("kokoro-82m")!.Capabilities!.MaxSpeed);
        Assert.True(catalog.FindModel("kokoro-82m")!.Capabilities!.Blending);

        var engine = catalog.FindEngine(EngineIds.QwenTts)!;
        Assert.Equal("python", engine.Kind);
        Assert.Equal("zip-tree", engine.Downloads.Single().Kind);
        var ort = catalog.FindEngine(EngineIds.OrtCuda)!;
        Assert.Contains(EngineIds.QwenTts, ort.Requires!);
        Assert.Contains(ort.Files, f => f.Path.EndsWith("onnxruntime_providers_cuda.dll"));
    }

    [Fact]
    public void Embedded_lock_pins_every_package_with_hashes()
    {
        using var stream = typeof(EngineInstaller).Assembly.GetManifestResourceStream("NoorVox.Core.Engines.qwen-tts.lock")!;
        var lines = new StreamReader(stream).ReadToEnd().Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.True(lines.Count > 50);
        Assert.All(lines, l => Assert.Matches(@"^[A-Za-z0-9_.\-]+==\S+( --hash=sha256:[0-9a-f]{64})+$", l));
        Assert.Contains(lines, l => l.StartsWith("transformers==5.15.1 ")); // 5.23 breaks faster-qwen3-tts (ADR-0002)
        Assert.Contains(lines, l => l.StartsWith("torch==2.11.0+cu128 "));
    }

    [Fact]
    public void Without_a_default_the_recommended_model_wins_over_alphabetical_order()
    {
        using var home = new TestHome();
        var s = home.Storage();
        foreach (var (id, backend) in new[] { ("chatterbox-english", "chatterbox"), ("kokoro-82m", "kokoro-onnx") })
        {
            var dir = Path.Combine(s.ModelsDir, id, "1.0");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName),
                Json.Serialize(new PackageManifest { Id = id, Version = "1.0", Backend = backend, Files = [new("model.safetensors", 1, new string('a', 64))] }));
        }
        Assert.Equal("kokoro-82m", new ModelRegistry(s).Resolve(null).Id); // "chatterbox-english" sorts first but must not become the default
    }

    [Fact]
    public void Catalog_capabilities_override_a_stale_installed_manifest()
    {
        // Installed before the pace minimum was corrected: the manifest still says 0, which crashes Chatterbox.
        var stale = new PackageManifest
        {
            Id = "chatterbox-english", Version = "1.0", Backend = "chatterbox",
            Capabilities = new ModelCapabilities { Cloning = true, Pace = true, PaceMin = 0.0, PaceMax = 1.0, Emotion = true, EmotionMin = 0.25, EmotionMax = 2.0 },
        };
        Assert.Equal(0.1, ModelCapabilities.For(stale).PaceMin);
        var unknown = new PackageManifest { Id = "somebody-elses-model", Version = "1.0", Backend = "chatterbox", Capabilities = new ModelCapabilities { PaceMin = 0.0 } };
        Assert.Equal(0.0, ModelCapabilities.For(unknown).PaceMin);   // not in the catalog: the manifest stays the authority
    }

    [Fact]
    public void Old_kokoro_manifests_keep_their_capabilities_and_primary_file()
    {
        var m = new PackageManifest { Id = "kokoro-82m", Version = "1.0", Files = [new("kokoro.onnx", 1, new string('a', 64))] };
        var k = ModelCapabilities.For(m);   // the catalog's Kokoro entry (it knows kokoro-82m@1.0)
        Assert.Equal((true, 0.5, 2.0, true, false), (k.Speed, k.MinSpeed, k.MaxSpeed, k.Blending, k.Style));
        var unknownOld = new PackageManifest { Id = "old-kokoro-import", Version = "0.9", Backend = "kokoro-onnx" };
        Assert.Same(ModelCapabilities.Kokoro, ModelCapabilities.For(unknownOld)); // not in the catalog, no block: the built-in Kokoro fallback
        var q = WriteModel(QwenManifest("qwen-x", CustomCaps));
        Assert.Equal("model.safetensors", q.PrimaryFile.Path);
        Assert.Equal(new string('b', 64), q.ModelSha256);
    }

    [Fact]
    public void Capabilities_reject_controls_the_model_does_not_have()
    {
        var custom = WriteModel(QwenManifest("qwen-custom", CustomCaps, [new("aiden", "Aiden", "male", "english", "")]));
        NarrationService.CheckCapabilities(custom, 1.0, "calm", 42, "english"); // all fine
        Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(custom, 1.2, null, null, null)).Code);
        Assert.Equal(ErrorCodes.InvalidArguments, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(custom, 1.0, null, null, "klingon")).Code);

        var clone = WriteModel(QwenManifest("qwen-clone", CloneCaps));
        Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(clone, 1.0, "angry", null, null)).Code);

        var kokoro = WriteModel(new PackageManifest { Id = "kokoro-82m", Version = "1.0", Backend = "kokoro-onnx", Files = [new("kokoro.onnx", 1, new string('a', 64))] });
        NarrationService.CheckCapabilities(kokoro, 1.5, null, null, null);
        Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(kokoro, 1.0, null, 7, null)).Code);
        Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(kokoro, 1.0, null, null, "english")).Code);
        Assert.Equal(ErrorCodes.InvalidArguments, Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(kokoro, 3.0, null, null, null)).Code);
    }

    [Fact]
    public void Voice_profiles_need_consent_are_immutable_and_belong_to_cloning_models_only()
    {
        var models = Path.Combine(_root, "models");
        var reference = Wav("ref.wav", 4);
        var noConsent = Assert.Throws<NoorVoxException>(() => VoiceProfiles.Create(models, new("Tom", reference, "Hello there.", "")));
        Assert.Contains("consent", noConsent.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NoorVoxException>(() => VoiceProfiles.Create(models, new("Tom", Wav("short.wav", 1), "Hi.", "own-voice")));
        Assert.Throws<NoorVoxException>(() => VoiceProfiles.Create(models, new("Tom", reference, "", "own-voice")));

        var p = VoiceProfiles.Create(models, new("Tom Narrator", reference, "Hello there, this is me.", "own-voice", "english", "male"));
        Assert.Equal("tom_narrator", p.VoiceId);
        Assert.Equal(VoiceProfiles.ConsentStatements["own-voice"], p.Consent.Statement);
        Assert.Equal(4000, p.ReferenceDurationMs);
        Assert.True(File.Exists(p.ReferencePath));
        Assert.Equal(NoorVox.Core.Util.FileUtil.Sha256File(reference), p.ReferenceSha256);
        Assert.Equal("ref.wav", p.SourceFileName); // never the full original path
        Assert.Equal(ErrorCodes.OutputExists, Assert.Throws<NoorVoxException>(() => VoiceProfiles.Create(models, new("Tom Narrator", reference, "x y", "own-voice"))).Code);

        var clone = WriteModel(QwenManifest("qwen-clone", CloneCaps));
        var custom = WriteModel(QwenManifest("qwen-custom", CustomCaps, [new("aiden", "Aiden", "male", "english", "")]));
        Assert.True(clone.HasVoice("tom_narrator"));
        Assert.False(custom.HasVoice("tom_narrator"));
        Assert.True(custom.HasVoice("aiden"));
        var info = ModelVoices.List(clone).Single();
        Assert.Equal("clone", info.Kind);
        Assert.Equal("Tom Narrator", info.DisplayName);
        Assert.Equal("built-in", ModelVoices.List(custom).Single().Kind);

        VoiceProfiles.Remove(models, "tom_narrator");
        Assert.False(ModelRegistry.ReadPackage(clone.Directory).HasVoice("tom_narrator"));
    }

    [Fact]
    public void Style_changes_the_cache_key_only_when_present_and_supported()
    {
        var kokoro = WriteModel(new PackageManifest { Id = "kokoro-82m", Version = "1.0", Backend = "kokoro-onnx", Files = [new("kokoro.onnx", 1, new string('a', 64))] });
        // Pre-0.3 formula: unchanged for takes without a style.
        var material = $"v1\nHello\naf_heart\n1\n{kokoro.ModelSha256}\nkokoro-onnx";
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        Assert.Equal(expected, ProjectStore.CacheKey("Hello", "af_heart", 1.0, kokoro));
        Assert.NotEqual(expected, ProjectStore.CacheKey("Hello", "af_heart", 1.0, kokoro, "calm"));

        var dir = Path.Combine(_root, "proj");
        var store = ProjectStore.Create(dir, "Style test", "narration", "aiden");
        var custom = WriteModel(QwenManifest("qwen-custom", CustomCaps, [new("aiden", "Aiden", "male", "english", "")]));
        store.MutateProject(p => p.Defaults.Style = "calm and warm");
        var seg = new Segment { SegmentId = "s1", Text = "Hello." };
        Assert.Equal("calm and warm", store.Effective(seg, custom).Style);
        seg.Style = "whispering";
        Assert.Equal("whispering", store.Effective(seg, custom).Style);
        Assert.Null(store.Effective(seg, kokoro).Style); // Kokoro can't take a style: ignored, key unchanged
    }

    [Fact]
    public void Engine_archive_extraction_rejects_paths_outside_the_target()
    {
        var zip = Path.Combine(_root, "evil.zip");
        Directory.CreateDirectory(_root);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("tools/python.exe").Open())) w.Write("ok");
            using (var w = new StreamWriter(z.CreateEntry("tools/../../escape.txt").Open())) w.Write("bad");
        }
        Assert.Throws<InvalidDataException>(() => EngineInstaller.ExtractTree(zip, "tools/", Path.Combine(_root, "out")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public void Load_options_require_the_engine_and_pick_kokoro_cuda_only_when_asked()
    {
        using var home = new TestHome();
        var s = home.Storage();
        var dir = Path.Combine(s.ModelsDir, "qwen-custom", "1.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(QwenManifest("qwen-custom", CustomCaps)));
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");
        var qwen = ModelRegistry.ReadPackage(dir);
        Assert.Equal(ErrorCodes.EngineNotInstalled, Assert.Throws<NoorVoxException>(() => sup.LoadOptionsFor(qwen)).Code);

        var engineDir = new EngineRegistry(s).EngineDir(EngineIds.QwenTts, "1.0");
        Directory.CreateDirectory(engineDir);
        File.WriteAllText(Path.Combine(engineDir, EngineRegistry.ManifestName), Json.Serialize(new EngineManifest { Id = EngineIds.QwenTts, Version = "1.0", Kind = "python" }));
        var o = sup.LoadOptionsFor(qwen);
        Assert.Equal(engineDir, o.EngineDirectory);
        Assert.EndsWith(Path.Combine("engines", "qwen", "noorvox_qwen.py"), o.SidecarScript);
        Assert.True(File.Exists(o.SidecarScript)); // shipped next to the worker

        var kokoroDir = Path.Combine(s.ModelsDir, "kokoro-82m", "1.0");
        Directory.CreateDirectory(kokoroDir);
        File.WriteAllText(Path.Combine(kokoroDir, ModelRegistry.ModelManifestName),
            Json.Serialize(new PackageManifest { Id = "kokoro-82m", Version = "1.0", Backend = "kokoro-onnx", Files = [new("kokoro.onnx", 1, new string('a', 64))] }));
        var kokoro = ModelRegistry.ReadPackage(kokoroDir);
        Assert.Equal("cpu", sup.LoadOptionsFor(kokoro).ExecutionProvider);
        s.Settings.Gpu.KokoroProvider = "cuda";
        var cuda = sup.LoadOptionsFor(kokoro);
        Assert.Equal("cuda", cuda.ExecutionProvider);
        Assert.Null(cuda.OrtCudaDirectory); // not installed: the backend will fall back to CPU and say so
        Assert.Single(cuda.CudaLibraryDirectories!);
    }

    /// <summary>
    /// Real Qwen synthesis through the worker and the Python sidecar. Opt-in (needs an NVIDIA GPU, the qwen-tts engine and
    /// the 1.7B CustomVoice model in NOORVOX_TEST_MODELS_DIR, and minutes to load): set NOORVOX_TEST_QWEN=1.
    /// </summary>
    [Fact]
    public async Task Real_qwen_synthesis_is_reproducible_with_a_seed()
    {
        if (TestPaths.ModelsDir is not { } models || Environment.GetEnvironmentVariable("NOORVOX_TEST_QWEN") != "1") return;
        using var home = new TestHome();
        var s = home.Storage();
        s.SetPath(NoorVox.Core.Storage.StorageKind.Models, models);
        var model = new ModelRegistry(s).Find("qwen3-tts-1.7b-customvoice");
        if (model is null || new EngineRegistry(s).Find(EngineIds.QwenTts) is null) return;
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");
        var svc = new NarrationService(s, sup);
        var outDir = Path.Combine(_root, "out");
        async Task<NarrationResult> Say(string name) => await svc.SynthesizeAsync(new NarrationRequest
        {
            Text = "Testing, one two three.", VoiceId = "aiden", Model = model.Reference, Style = "Calm and clear.", Seed = 1234,
            Language = "english", OutputPath = Path.Combine(outDir, name), WorkerId = "qwen-test", KeepWorker = true,
        }, null, CancellationToken.None);
        try
        {
            var a = await Say("a.wav");
            var b = await Say("b.wav");
            Assert.Equal(1234, a.Seed);
            Assert.Equal("CUDA", a.Backend.ExecutionProvider);
            Assert.True(a.DurationMs > 500);
            Assert.Equal(File.ReadAllBytes(a.OutputPath).Skip(44), File.ReadAllBytes(b.OutputPath).Skip(44));
            var st = await sup.GetStatusAsync("qwen-test", CancellationToken.None);
            Assert.NotNull(st.Resources?.GpuMemory);
            Assert.NotNull(st.Resources?.Sidecar);
            var sidecarPid = st.Resources!.Sidecar!.Pid;
            await sup.StopAsync("qwen-test", false, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(sidecarPid), TimeSpan.FromSeconds(15)), "the Python engine must not outlive its worker");
        }
        finally { await sup.StopAllAsync(true, TimeSpan.FromSeconds(10), CancellationToken.None); }
    }
}

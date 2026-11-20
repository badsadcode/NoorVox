using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;

namespace NoorVox.Tests;

/// <summary>Voice blending, segment split/merge and pronunciation editing.</summary>
public class FeatureTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "feat-" + Guid.NewGuid().ToString("N")[..8]);

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

    /// <summary>A model folder with three tiny constant-valued voices: bm_one = 1.0, bm_three = 3.0, af_five = 5.0.</summary>
    InstalledModel FakeModelWithVoices()
    {
        var dir = Path.Combine(_root, "model");
        Directory.CreateDirectory(Path.Combine(dir, "voices"));
        Directory.CreateDirectory(Path.Combine(dir, "voicepacks"));
        var files = new List<PackageFile>();
        foreach (var (id, v) in new[] { ("bm_one", 1f), ("bm_three", 3f), ("af_five", 5f) })
        {
            var path = Path.Combine(dir, "voices", id + ".npy");
            NpyFile.Write(path, [4, 1, 2], Enumerable.Repeat(v, 8).ToArray());
            files.Add(new PackageFile($"voices/{id}.npy", new FileInfo(path).Length, NoorVox.Core.Util.FileUtil.Sha256File(path)));
        }
        var model = new PackageManifest { Id = "kokoro-82m", Version = "1.0", Backend = "kokoro-onnx", DefaultVoice = "bm_one", Files = [new("kokoro.onnx", 1, new string('a', 64))] };
        File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(model));
        File.WriteAllText(Path.Combine(dir, "voicepacks", "std.json"), Json.Serialize(new PackageManifest { Kind = "voicePack", Id = "std", Files = files }));
        return ModelRegistry.ReadPackage(dir);
    }

    [Fact]
    public void Npy_round_trips_and_matches_numpy_layout()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "x.npy");
        NpyFile.Write(path, [510, 1, 256], Enumerable.Range(0, 510 * 256).Select(i => i * 0.001f).ToArray());
        Assert.Equal(522368, new FileInfo(path).Length); // the same size as Kokoro's own voice files
        var (shape, data) = NpyFile.Read(path);
        Assert.Equal([510, 1, 256], shape);
        Assert.Equal(12.345f, data[12345], 3);
    }

    [Fact]
    public void Blend_is_a_normalised_weighted_average_registered_as_a_hashed_immutable_voice()
    {
        var model = FakeModelWithVoices();
        var r = VoiceBlender.Blend(model, "My Narrator!", [new BlendPart("bm_one", 3), new BlendPart("bm_three", 1)]);

        Assert.Equal("bm_mynarrator", r.VoiceId);
        Assert.Equal([0.75, 0.25], r.Parts.Select(p => p.Weight));
        var (_, data) = NpyFile.Read(r.Path);
        Assert.All(data, v => Assert.Equal(1.5f, v, 5));                                  // 0.75·1 + 0.25·3

        var reloaded = ModelRegistry.ReadPackage(model.Directory);
        var info = ModelVoices.List(reloaded).Single(v => v.Id == "bm_mynarrator");
        Assert.Equal("My Narrator!", info.DisplayName);
        Assert.Equal(VoiceBlender.CustomPackId, info.PackId);
        Assert.True(ModelRegistry.Verify(reloaded.Directory, reloaded.VoicePacks.SelectMany(p => p.Files), full: true).Ok);

        var ex = Assert.Throws<NoorVoxException>(() => VoiceBlender.Blend(reloaded, "My Narrator", [new BlendPart("bm_one", 1), new BlendPart("af_five", 1)]));
        Assert.Equal(ErrorCodes.OutputExists, ex.Code);                                     // immutable: same id refused
    }

    [Fact]
    public void Dominant_voice_sets_language_and_bad_blends_are_rejected()
    {
        var model = FakeModelWithVoices();
        Assert.Equal("af_mix", VoiceBlender.Blend(model, "mix", [new BlendPart("bm_one", 1), new BlendPart("af_five", 2)]).VoiceId);
        model = ModelRegistry.ReadPackage(model.Directory);
        Assert.Throws<NoorVoxException>(() => VoiceBlender.Blend(model, "a", [new BlendPart("bm_one", 1)]));
        Assert.Throws<NoorVoxException>(() => VoiceBlender.Blend(model, "b", [new BlendPart("bm_one", 1), new BlendPart("bm_one", 1)]));
        Assert.Throws<NoorVoxException>(() => VoiceBlender.Blend(model, "c", [new BlendPart("bm_one", 1), new BlendPart("bm_three", 0)]));
        Assert.Throws<NoorVoxException>(() => VoiceBlender.Blend(model, "d", [new BlendPart("bm_one", 1), new BlendPart("zz_nobody", 1)]));
        Assert.Throws<NoorVoxException>(() => VoiceBlender.Blend(model, "!!!", [new BlendPart("bm_one", 1), new BlendPart("bm_three", 1)]));
    }

    ProjectStore Project()
    {
        Directory.CreateDirectory(_root);
        var p = ProjectStore.Create(Path.Combine(_root, "p-" + Guid.NewGuid().ToString("N")[..4]), "P", "documentary", "bm_one");
        var md = Path.Combine(_root, $"{Guid.NewGuid():N}.md");
        File.WriteAllText(md, "# Ep\n\n## Scene\n\nFirst sentence. Second sentence.\n\nThird paragraph.\n\n## Other\n\nElsewhere.\n");
        ScriptImporter.ImportInto(p, md);
        return p;
    }

    [Fact]
    public void Split_keeps_the_id_and_approval_pointer_and_inserts_a_new_segment()
    {
        var p = Project();
        p.UpdateSegment("ep01.s01.p001", s => { s.ApprovedTakeId = "t-old"; s.PauseAfterMs = 700; });
        var (first, second) = p.SplitSegment("ep01.s01.p001", "First sentence.".Length);

        Assert.Equal("ep01.s01.p001", first.SegmentId);
        Assert.Equal("First sentence.", first.Text);
        Assert.Equal("t-old", first.ApprovedTakeId);              // kept; becomes stale because the text changed
        Assert.Null(first.PauseAfterMs);
        Assert.Equal("Second sentence.", second.Text);
        Assert.Equal(700, second.PauseAfterMs);                   // the pause after the original now follows the second part
        Assert.Equal(["ep01.s01.p001", second.SegmentId, "ep01.s01.p002"], p.LoadSection("ep01").Scenes[0].Segments.Select(s => s.SegmentId));
        Assert.Throws<NoorVoxException>(() => p.SplitSegment("ep01.s01.p001", 0));
    }

    [Fact]
    public void Merge_joins_with_the_next_segment_of_the_same_scene_only()
    {
        var p = Project();
        p.UpdateSegment("ep01.s01.p002", s => s.Notes = "visual: map");
        var merged = p.MergeWithNext("ep01.s01.p001");
        Assert.Equal("First sentence. Second sentence. Third paragraph.", merged.Text);
        Assert.Equal("visual: map", merged.Notes);
        Assert.Single(p.LoadSection("ep01").Scenes[0].Segments);
        var ex = Assert.Throws<NoorVoxException>(() => p.MergeWithNext("ep01.s01.p001")); // last in scene: never crosses into the next scene
        Assert.Contains("last segment", ex.Message);
    }

    [Fact]
    public void Pronunciation_rules_are_validated_and_report_affected_segments()
    {
        var p = Project();
        int changed = p.SavePronunciation([new PronunciationRule { Match = "sentence", Replace = "sen-tence", WholeWord = true }]);
        Assert.Equal(1, changed);
        Assert.Equal("First sen-tence. Second sen-tence.", p.Pronunciation().Apply(p.Find("ep01.s01.p001").Segment.Text));
        Assert.Throws<NoorVoxException>(() => p.SavePronunciation([new PronunciationRule { Match = " ", Replace = "x" }]));
        Assert.Throws<NoorVoxException>(() => p.SavePronunciation([new PronunciationRule { Match = "A", Replace = "1" }, new PronunciationRule { Match = "a", Replace = "2" }]));
        Assert.Equal(1, p.SavePronunciation([]));                 // removing it changes the same segment back
    }
}

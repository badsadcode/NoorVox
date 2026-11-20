using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Models;
using NoorVox.Core.Narration;
using NoorVox.Core.Storage;

namespace NoorVox.Tests;

/// <summary>Voice lab (0.3.1): recordings library, designed/synthetic voice profiles, VoiceDesign capability.</summary>
public class VoiceLabTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "noorvox-tests", "lab-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    static byte[] WavBytes(double seconds, int rate = 48000)
    {
        var path = Path.Combine(Path.GetTempPath(), $"noorvox-wav-{Guid.NewGuid():N}.wav"); // WavWriter never overwrites
        try { WavWriter.WritePcm16(path, new byte[(int)(seconds * rate) * 2], rate); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Recordings_are_saved_listed_updated_and_never_overwritten()
    {
        using var home = new TestHome();
        var s = home.Storage();
        s.SetPath(StorageKind.Recordings, Path.Combine(_root, "rec"));
        var a = RecordingStore.Save(s, WavBytes(4), "Tom, calm narrator", "Hello there.", "microphone");
        var b = RecordingStore.Save(s, WavBytes(3), "Tom, calm narrator", "", "microphone");
        Assert.Equal("tom-calm-narrator", a.Id);
        Assert.Equal("tom-calm-narrator-2", b.Id); // same name: a new file, never an overwrite
        Assert.Equal(4000, a.DurationMs);
        Assert.Equal(48000, a.SampleRate);
        Assert.True(File.Exists(Path.ChangeExtension(a.WavPath, ".json")));
        Assert.Equal(2, RecordingStore.List(s).Count);

        var before = File.ReadAllBytes(a.WavPath);
        RecordingStore.Update(s, a.Id, "Renamed", "Hello there, Tom.", "take 3 was best");
        var r = RecordingStore.Find(s, a.Id);
        Assert.Equal(("Renamed", "Hello there, Tom.", "take 3 was best"), (r.Name, r.Transcript, r.Notes));
        Assert.Equal(before, File.ReadAllBytes(a.WavPath)); // metadata only; the audio never changes

        Assert.Throws<NoorVoxException>(() => RecordingStore.Save(s, [1, 2, 3], "junk", null, "microphone"));
        Assert.Throws<NoorVoxException>(() => RecordingStore.Save(s, WavBytes(0.2), "too short", null, "microphone"));
        Assert.Throws<NoorVoxException>(() => RecordingStore.Find(s, "../escape"));
        Assert.Equal(2, Directory.GetFiles(RecordingStore.Dir(s), "*.wav").Length); // rejected saves leave nothing behind
    }

    [Fact]
    public void A_wav_copied_into_the_folder_by_hand_is_listed_too()
    {
        using var home = new TestHome();
        var s = home.Storage();
        s.SetPath(StorageKind.Recordings, Path.Combine(_root, "rec2"));
        Directory.CreateDirectory(RecordingStore.Dir(s));
        File.WriteAllBytes(Path.Combine(RecordingStore.Dir(s), "my-take.wav"), WavBytes(2.5));
        var r = Assert.Single(RecordingStore.List(s));
        Assert.Equal(("my-take", "imported", 2500L), (r.Id, r.Source, r.DurationMs));
    }

    [Fact]
    public void Generated_profiles_record_provenance_instead_of_consent_and_appear_as_their_kind()
    {
        var models = Path.Combine(_root, "models");
        var wav = Path.Combine(_root, "sample.wav");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(wav, WavBytes(6, 24000));
        var p = VoiceProfiles.CreateGenerated(models, new GeneratedProfileRequest("Margaret", wav, "Hello, I tell stories.", "designed",
            "qwen3-tts-1.7b-voicedesign@1.0", "designed", "A warm British woman in her fifties.", "english", "female"));
        Assert.Equal(("margaret", "designed", "designed"), (p.VoiceId, p.Kind, p.Consent.Basis));
        Assert.Contains("no real person", p.Consent.Statement);
        Assert.Equal("A warm British woman in her fifties.", p.Description);
        Assert.Throws<NoorVoxException>(() => VoiceProfiles.CreateGenerated(models, new GeneratedProfileRequest("X", wav, "Hi there.", "clone", "m", "v", null)));
        // The person-consent path can't be satisfied with a "generated" basis.
        Assert.Throws<NoorVoxException>(() => VoiceProfiles.Create(models, new CreateProfileRequest("Y", wav, "Hi there.", "designed")));

        var baseDir = Path.Combine(models, "qwen-base", "1.0");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, ModelRegistry.ModelManifestName), Json.Serialize(new PackageManifest
        {
            Id = "qwen-base", Version = "1.0", Backend = "qwen3-tts", Capabilities = new ModelCapabilities { Cloning = true, Seed = true, Languages = ["auto", "english"] },
            Files = [new("model.safetensors", 1, new string('b', 64))],
        }));
        var info = Assert.Single(ModelVoices.List(ModelRegistry.ReadPackage(baseDir)));
        Assert.Equal("designed", info.Kind);
        Assert.Contains("A warm British woman", info.Description);
    }

    [Fact]
    public void VoiceDesign_requires_a_description_and_is_in_the_catalog()
    {
        var m = ModelCatalog.LoadEmbedded().FindModel("qwen3-tts-1.7b-voicedesign")!;
        Assert.True(m.Capabilities!.Design);
        Assert.False(m.Capabilities.Cloning || m.Capabilities.Style || m.Capabilities.Speed);
        Assert.Equal("designed", Assert.Single(m.BuiltInVoices!).Id);

        var dir = Path.Combine(_root, "vd", "1.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModelRegistry.ModelManifestName), Json.Serialize(new PackageManifest
        {
            Id = "vd", Version = "1.0", Backend = "qwen3-tts", Capabilities = m.Capabilities, BuiltInVoices = m.BuiltInVoices, Files = m.Files,
        }));
        var installed = ModelRegistry.ReadPackage(dir);
        Assert.Throws<NoorVoxException>(() => NarrationService.CheckCapabilities(installed, 1.0, null, null, "english"));
        NarrationService.CheckCapabilities(installed, 1.0, "A gravelly old sea captain.", 5, "english");
    }
}

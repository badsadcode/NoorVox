using NoorVox.Core.Audio;
using NoorVox.Core.Models;

namespace NoorVox.Tests;

public class WavTests
{
    [Fact]
    public void Duration_is_measured_from_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-{Guid.NewGuid():N}.wav");
        try
        {
            var pcm = new byte[24_000 * 2 * 3 / 2]; // 1.5 s of mono 16-bit at 24 kHz
            WavWriter.WritePcm16(path, pcm, 24_000);
            var info = WavInfo.Read(path);
            Assert.Equal(24_000, info.SampleRate);
            Assert.Equal(1, info.Channels);
            Assert.Equal(16, info.BitsPerSample);
            Assert.Equal(36_000, info.Samples);
            Assert.Equal(1500, info.DurationMs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_non_wav_and_empty_audio()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllText(path, new string('x', 100));
            Assert.Throws<InvalidDataException>(() => WavInfo.Read(path));
            File.Delete(path);
            WavWriter.WritePcm16(path, [], 24_000);
            Assert.Throws<InvalidDataException>(() => WavInfo.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Writer_never_overwrites()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WritePcm16(path, new byte[4], 24_000);
            Assert.Throws<IOException>(() => WavWriter.WritePcm16(path, new byte[4], 24_000));
        }
        finally { File.Delete(path); }
    }
}

public class CatalogTests
{
    [Fact]
    public void Embedded_catalog_is_complete_and_hashed()
    {
        var c = ModelCatalog.LoadEmbedded();
        var kokoro = c.FindModel("kokoro-82m");
        Assert.NotNull(kokoro);
        Assert.Equal("Apache-2.0", kokoro!.License);
        Assert.Contains("kokoro-82m-v1.0-voices", kokoro.DefaultVoicePacks);
        var pack = c.FindVoicePack("kokoro-82m-v1.0-voices")!;
        Assert.Equal(54, pack.Files.Count);
        Assert.Contains(pack.Files, f => f.Path == $"voices/{kokoro.DefaultVoice}.npy");
        foreach (var item in new ICatalogItem[] { kokoro, pack })
        {
            Assert.All(item.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
            Assert.All(item.Downloads, d => { Assert.StartsWith("https://", d.Url); Assert.Matches("^[0-9a-f]{64}$", d.Sha256); });
        }
    }

    [Fact]
    public void Model_references_parse()
    {
        Assert.Equal(("kokoro-82m", (string?)null), ModelRef.Parse("kokoro-82m"));
        Assert.Equal(("kokoro-82m", "1.0"), ModelRef.Parse("kokoro-82m@1.0"));
        Assert.Null(ModelCatalog.LoadEmbedded().FindModel("kokoro-82m@9.9"));
    }
}

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using NoorVox.Core;
using NoorVox.Core.Models;

namespace NoorVox.Tests;

/// <summary>Installer behaviour against a fake HTTP server: verify, resume, reject corruption, import by hash.</summary>
public class InstallerTests
{
    sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Requests { get; } = [];
        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (!Files.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            LastRangeFrom = request.Headers.Range is null ? null : from;
            var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data[(int)from..]),
            };
            return Task.FromResult(resp);
        }
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    static (ModelCatalog Catalog, FakeServer Server, byte[] Weights) Build(bool corruptVoices = false)
    {
        var weights = RandomNumberGenerator.GetBytes(300_000);
        var voiceA = RandomNumberGenerator.GetBytes(5_000);
        var voiceB = RandomNumberGenerator.GetBytes(5_000);
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in new[] { ("voices/af_test.npy", voiceA), ("voices/am_test.npy", voiceB), ("voices/README.md", "x"u8.ToArray()), ("../evil.txt", "x"u8.ToArray()) })
            {
                using var e = zip.CreateEntry(name).Open();
                e.Write(data);
            }
        }
        var zipBytes = zipStream.ToArray();
        var server = new FakeServer();
        server.Files["https://example.test/model.onnx"] = weights;
        server.Files["https://example.test/voices.zip"] = corruptVoices ? zipBytes[..^10].Concat(new byte[10]).ToArray() : zipBytes;

        var model = new CatalogModel("test-model", "1.0", "Test model", "", "kokoro-onnx", true, "Apache-2.0", "https://example.test/license",
            "https://example.test", "af_test", 24000, ["test-voices"],
            [new("https://example.test/model.onnx", weights.Length, Sha(weights), "file", "model.onnx")],
            [new("model.onnx", weights.Length, Sha(weights))]);
        var pack = new CatalogVoicePack("test-voices", "1.0", "test-model", ["1.0"], "Test voices", "", "Apache-2.0", "", "https://example.test",
            [new("https://example.test/voices.zip", zipBytes.Length, Sha(zipBytes), "zip", ".")],
            [new("voices/af_test.npy", voiceA.Length, Sha(voiceA)), new("voices/am_test.npy", voiceB.Length, Sha(voiceB))]);
        return (new ModelCatalog(1, "test", [model], [pack]), server, weights);
    }

    [Fact]
    public async Task Install_downloads_verifies_and_registers_model_with_voice_pack()
    {
        using var home = new TestHome();
        var (catalog, server, _) = Build();
        var s = home.Storage();
        var installer = new ModelInstaller(s, catalog, new HttpClient(server));

        var result = await installer.InstallModelAsync(catalog.Models[0], withDefaultVoicePacks: true, null, CancellationToken.None);

        Assert.Equal("installed", result.Status);
        Assert.Equal("installed", result.VoicePacks![0].Status);
        var installed = new ModelRegistry(s).Resolve("test-model");
        Assert.Equal(["af_test", "am_test"], installed.Voices.Select(v => v.VoiceId));
        Assert.False(File.Exists(Path.Combine(installed.Directory, "voices", "README.md"))); // only catalog-listed files
        Assert.False(File.Exists(Path.Combine(s.ModelsDir, "test-model", "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(s.CacheDir, "staging", "test-model-1.0"))); // staging cleaned
        Assert.True(ModelRegistry.Verify(installed.Directory, installed.Manifest.Files, full: true).Ok);

        var again = await installer.InstallModelAsync(catalog.Models[0], true, null, CancellationToken.None);
        Assert.Equal("alreadyInstalled", again.Status);
        Assert.Equal(0, again.BytesDownloaded); // never re-downloads what is installed
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_http_range()
    {
        using var home = new TestHome();
        var (catalog, server, weights) = Build();
        var s = home.Storage();
        var staging = Path.Combine(s.CacheDir, "staging", "test-model-1.0");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "model.onnx.part"), weights[..100_000]);

        var result = await new ModelInstaller(s, catalog, new HttpClient(server)).InstallModelAsync(catalog.Models[0], false, null, CancellationToken.None);

        Assert.Equal(100_000, server.LastRangeFrom);
        Assert.Equal(weights.Length - 100_000, result.BytesDownloaded);
        Assert.True(new ModelRegistry(s).Find("test-model@1.0") is not null);
    }

    [Fact]
    public async Task Corrupt_download_is_rejected_and_never_activated()
    {
        using var home = new TestHome();
        var (catalog, server, _) = Build(corruptVoices: true);
        var s = home.Storage();
        var installer = new ModelInstaller(s, catalog, new HttpClient(server));

        var ex = await Assert.ThrowsAsync<NoorVoxException>(() => installer.InstallModelAsync(catalog.Models[0], true, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.VerificationFailed, ex.Code);
        var model = new ModelRegistry(s).Find("test-model")!; // the model itself verified fine
        Assert.Empty(model.VoicePacks);                        // the corrupt pack was not registered
        Assert.False(File.Exists(Path.Combine(s.CacheDir, "staging", "test-model-1.0", "voices.zip.part")));
    }

    [Fact]
    public async Task Import_identifies_model_by_hash_and_rejects_unknown_weights()
    {
        using var home = new TestHome();
        var (catalog, server, weights) = Build();
        var s = home.Storage();
        var source = Path.Combine(home.Root, "manual");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "whatever-name.onnx"), weights);
        var installer = new ModelInstaller(s, catalog, new HttpClient(server));

        var result = await installer.ImportAsync(source, null, CancellationToken.None);
        Assert.Equal("test-model", result.Id);
        Assert.Equal("import", new ModelRegistry(s).Find("test-model")!.Manifest.Origin);
        Assert.Empty(server.Requests); // import never touches the network

        var bogus = Path.Combine(home.Root, "bogus");
        Directory.CreateDirectory(bogus);
        File.WriteAllBytes(Path.Combine(bogus, "model.onnx"), RandomNumberGenerator.GetBytes(300_000));
        var ex = await Assert.ThrowsAsync<NoorVoxException>(() => installer.ImportAsync(bogus, null, CancellationToken.None));
        Assert.Equal(ErrorCodes.UnknownModel, ex.Code);
    }
}

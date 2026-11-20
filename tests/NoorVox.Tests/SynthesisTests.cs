using System.Diagnostics;
using System.Text.Json;
using NoorVox.Core.Audio;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Tests;

/// <summary>
/// Real offline synthesis with the installed Kokoro model. Set NOORVOX_TEST_MODELS_DIR to a models root that contains
/// kokoro-82m (e.g. E:\AI\NoorVox\Models); otherwise these tests are skipped (the model is not in the repo).
/// </summary>
public class SynthesisTests
{
    static (TestHome Home, StorageContext Storage, InstalledModel Model) Setup()
    {
        var modelsDir = TestPaths.ModelsDir ?? throw SkipException();
        var home = new TestHome();
        var s = home.Storage();
        s.Settings.Paths.Models = modelsDir; // read-only use of the real models
        s.Save();
        s = home.Storage();
        var model = new ModelRegistry(s).Find("kokoro-82m");
        if (model is null) { home.Dispose(); throw SkipException(); }
        return (home, s, model);
    }

    static Exception SkipException()
    {
        Assert.Skip("NOORVOX_TEST_MODELS_DIR not set or has no kokoro-82m.");
        return new InvalidOperationException();
    }

    [Fact]
    public async Task Worker_synthesizes_valid_audio_and_recycle_reclaims_memory()
    {
        var (home, s, model) = Setup();
        using var _ = home;
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");
        await sup.StartAsync("synth", boundToLauncher: true, 0, CancellationToken.None);
        var loaded = await sup.LoadAsync("synth", model, CancellationToken.None);
        Assert.Equal("ready", loaded.State);
        Assert.Equal(model.ModelSha256, loaded.LoadedModel!.ModelSha256);

        var outPath = Path.Combine(home.Root, "out.wav");
        var r = await sup.SynthesizeAsync("synth", new SynthesizeParams("The first spark of NoorEngine.", "af_heart", 1.0, outPath), CancellationToken.None);

        var info = WavInfo.Read(outPath);
        Assert.Equal(24_000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(r.Samples, info.Samples);
        Assert.Equal(r.DurationMs, info.DurationMs);
        Assert.InRange(info.DurationSeconds, 1.0, 6.0);
        Assert.True(HasSignal(outPath), "audio is silent");

        var busy = await sup.GetStatusAsync("synth", CancellationToken.None);
        Assert.Equal(1, busy.JobsCompleted);
        var recycled = await sup.RecycleAsync("synth", CancellationToken.None);
        Assert.Null(recycled.LoadedModel);
        Assert.True(recycled.Resources!.WorkingSetBytes < busy.Resources!.WorkingSetBytes / 2,
            $"recycle should reclaim memory: {busy.Resources.WorkingSetBytes} -> {recycled.Resources.WorkingSetBytes}");
    }

    [Fact]
    public async Task Unknown_voice_is_a_clean_error_and_worker_stays_ready()
    {
        var (home, s, model) = Setup();
        using var _ = home;
        using var sup = new WorkerSupervisor(s, TestPaths.WorkerExe, "test");
        await sup.StartAsync("v", true, 0, CancellationToken.None);
        await sup.LoadAsync("v", model, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<NoorVox.Core.NoorVoxException>(() =>
            sup.SynthesizeAsync("v", new SynthesizeParams("hello", "../../etc", 1.0, Path.Combine(home.Root, "x.wav")), CancellationToken.None));
        Assert.Equal(NoorVox.Core.ErrorCodes.VoiceNotFound, ex.Code);
        Assert.Equal("ready", (await sup.GetStatusAsync("v", CancellationToken.None)).State);
    }

    [Fact]
    public async Task Cli_synth_reports_real_file_and_leaves_no_worker()
    {
        var (home, _, _) = Setup();
        using var __ = home;
        var outPath = Path.Combine(home.Root, "exports", "cli.wav");
        var (code, stdout) = await RunCli(home, $"synth --text \"No terrain, no trees, no Alden.\" --voice bm_george --out \"{outPath}\" --manifest --json --quiet");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(outPath, data.GetProperty("outputPath").GetString());
        Assert.Equal(WavInfo.Read(outPath).DurationMs, data.GetProperty("durationMs").GetInt64());
        Assert.True(File.Exists(data.GetProperty("manifestPath").GetString()));
        Assert.True(data.GetProperty("worker").GetProperty("stoppedAfter").GetBoolean());

        var (_, list) = await RunCli(home, "workers list --json");
        Assert.Empty(JsonDocument.Parse(list).RootElement.GetProperty("data").GetProperty("workers").EnumerateArray());
    }

    [Fact]
    public async Task Killing_the_cli_mid_synthesis_takes_its_one_shot_worker_with_it()
    {
        var (home, s, _) = Setup();
        using var _ = home;
        var text = string.Join(" ", Enumerable.Repeat("It started, as so many terrible ideas do, with a single image.", 40));
        var psi = CliStart(home, $"synth --text \"{text}\" --out \"{Path.Combine(home.Root, "long.wav")}\" --json --quiet");
        using var cli = Process.Start(psi)!;

        int workerPid = 0;
        Assert.True(await TestPaths.WaitUntil(() =>
        {
            var rec = Directory.Exists(s.WorkerRegistryDir) ? Directory.GetFiles(s.WorkerRegistryDir, "synth-*.json").FirstOrDefault() : null;
            if (rec is null) return false;
            workerPid = NoorVox.Core.Json.ReadFile<WorkerRecord>(rec).Pid;
            return workerPid > 0;
        }, TimeSpan.FromSeconds(30)), "one-shot worker never appeared");

        cli.Kill(); // simulate crash / Ctrl+Break / agent timeout
        Assert.True(await TestPaths.WaitUntil(() => !TestPaths.ProcessAlive(workerPid), TimeSpan.FromSeconds(10)), "orphaned worker survived its launcher");
        var (_, list) = await RunCli(home, "workers list --json");
        Assert.Empty(JsonDocument.Parse(list).RootElement.GetProperty("data").GetProperty("workers").EnumerateArray());
    }

    static bool HasSignal(string wav)
    {
        var bytes = File.ReadAllBytes(wav);
        int peak = 0;
        for (int i = 44; i + 1 < bytes.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, i)));
        return peak > 1000;
    }

    static ProcessStartInfo CliStart(TestHome home, string args)
    {
        var psi = new ProcessStartInfo(TestPaths.CliExe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["NOORVOX_HOME"] = home.Root;
        return psi;
    }

    static async Task<(int Code, string Stdout)> RunCli(TestHome home, string args)
    {
        using var p = Process.Start(CliStart(home, args))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        await stderr;
        return (p.ExitCode, await stdout);
    }
}

using System.Diagnostics;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Util;
using NoorVox.Core.Workers;

namespace NoorVox.Core.Narration;

public sealed record NarrationRequest
{
    public required string Text { get; init; }
    public string? VoiceId { get; init; }
    public double Speed { get; init; } = 1.0;
    public string? Model { get; init; }
    public required string OutputPath { get; init; }
    public bool Overwrite { get; init; }
    public bool WriteManifest { get; init; }

    /// <summary>CLI: explicit worker id (else reuse a warm <c>default</c> or use a one-shot worker).</summary>
    public string? WorkerId { get; init; }
    /// <summary>CLI: leave the worker running afterwards.</summary>
    public bool KeepWorker { get; init; }

    /// <summary>Studio: always use <see cref="WorkerId"/>; start it if needed with the settings below; never stop it afterwards.</summary>
    public bool DedicatedWorker { get; init; }
    public bool DedicatedBoundToLauncher { get; init; } = true;
    public int DedicatedIdleTimeoutSeconds { get; init; }
}

public sealed record TextInfo(int Chars, string Sha256);
public sealed record ModelProvenance(string Id, string Version, string ModelSha256, string License);
public sealed record BackendProvenance(string Id, string ExecutionProvider);
public sealed record WorkerUsage(string Id, bool Reused, bool StartedForRequest, bool StoppedAfter, string StateAfter);
public sealed record NarrationTimings(long SynthesisMs, long TotalMs, double RealTimeFactor);

/// <summary>Result of one narration: every number measured from the written file. Same shape as CLI <c>synth</c> data.</summary>
public sealed record NarrationResult(
    string OutputPath, long DurationMs, double DurationSeconds, int SampleRate, int Channels, int BitsPerSample, long Samples, long Bytes,
    TextInfo Text, string Voice, double Speed, ModelProvenance Model, BackendProvenance Backend, WorkerUsage Worker,
    NarrationTimings Timings, DateTimeOffset CreatedAt, string Generator, string? ManifestPath = null);

/// <summary>
/// Text → validated WAV at a chosen path, with provenance. Shared by the CLI (<c>noorvox synth</c>) and NoorVox Studio,
/// so both interfaces behave identically (spec principle 2).
/// </summary>
public sealed class NarrationService(StorageContext storage, WorkerSupervisor supervisor)
{
    public const string DefaultWorker = "default";
    public const double MinSpeed = 0.5, MaxSpeed = 2.0;

    public async Task<NarrationResult> SynthesizeAsync(NarrationRequest req, Action<string>? progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var text = req.Text.Trim();
        if (text.Length == 0) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is empty.");

        var model = new ModelRegistry(storage).Resolve(req.Model);
        var voiceId = req.VoiceId ?? storage.Settings.DefaultVoice ?? model.Manifest.DefaultVoice ?? model.Voices.FirstOrDefault().VoiceId
            ?? throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"{model.Reference} has no voices installed.");
        if (!model.Voices.Any(v => v.VoiceId == voiceId))
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{voiceId}' is not installed for {model.Reference}.",
                new { voiceId, hint = $"noorvox voices list --model {model.Reference}" });
        if (req.Speed is < MinSpeed or > MaxSpeed)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Speed must be between {MinSpeed} and {MaxSpeed} (Kokoro's supported range).");

        var outPath = Path.GetFullPath(req.OutputPath);
        if (!outPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Output must be a .wav file (WAV is the v1 output format).");
        if (File.Exists(outPath) && !req.Overwrite)
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"{outPath} already exists. Choose another name or allow overwriting.", new { outputPath = outPath });

        var tempDir = Path.Combine(storage.TempDir, "synth");
        Directory.CreateDirectory(tempDir);
        var tempWav = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");

        var (workerId, reused, started, keep) = await AcquireWorkerAsync(req, model, progress, ct);
        SynthesisResult result;
        try
        {
            progress?.Invoke($"Synthesizing {text.Length} chars with {voiceId} on worker '{workerId}'...");
            result = await supervisor.SynthesizeAsync(workerId, new SynthesizeParams(text, voiceId, req.Speed, tempWav), ct);
        }
        catch
        {
            TryDelete(tempWav);
            if (started && !keep) await SafeStopAsync(workerId);
            throw;
        }

        bool stoppedAfter = false;
        if (started && !keep)
        {
            await SafeStopAsync(workerId);
            stoppedAfter = true;
        }

        WavInfo info;
        try { info = WavInfo.Read(tempWav); }
        catch (InvalidDataException ex)
        {
            TryDelete(tempWav);
            throw new NoorVoxException(ErrorCodes.SynthesisFailed, ExitCode.SynthesisFailed, $"Worker output failed validation: {ex.Message}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.Move(tempWav, outPath, overwrite: req.Overwrite);
        total.Stop();

        var after = await supervisor.GetStatusAsync(workerId, CancellationToken.None);
        var narration = new NarrationResult(
            outPath, info.DurationMs, Math.Round(info.DurationSeconds, 3), info.SampleRate, info.Channels, info.BitsPerSample, info.Samples,
            new FileInfo(outPath).Length, new TextInfo(text.Length, FileUtil.Sha256Text(text)), voiceId, req.Speed,
            new ModelProvenance(model.Id, model.Version, model.ModelSha256, model.Manifest.License),
            new BackendProvenance(model.Manifest.Backend ?? "kokoro-onnx", after.ExecutionProvider ?? "CPU"),
            new WorkerUsage(workerId, reused, started, stoppedAfter, after.State),
            new NarrationTimings(result.ElapsedMs, total.ElapsedMilliseconds, Math.Round(result.ElapsedMs / Math.Max(1.0, info.DurationMs), 3)),
            DateTimeOffset.Now, $"NoorVox {NoorVoxInfo.Version}");

        if (req.WriteManifest)
        {
            var manifestPath = Path.ChangeExtension(outPath, ".noorvox.json");
            narration = narration with { ManifestPath = manifestPath };
            FileUtil.WriteAllTextAtomic(manifestPath, Json.Serialize(narration));
        }
        return narration;
    }

    async Task<(string Id, bool Reused, bool Started, bool Keep)> AcquireWorkerAsync(NarrationRequest req, InstalledModel model, Action<string>? progress, CancellationToken ct)
    {
        if (req.DedicatedWorker)
        {
            var dedicated = WorkerSupervisor.ValidateWorkerId(req.WorkerId ?? "studio");
            var ds = await supervisor.GetStatusAsync(dedicated, ct);
            bool startedNow = false;
            if (ds.State is "stopped" or "error")
            {
                progress?.Invoke("Starting voice engine...");
                ds = await supervisor.StartAsync(dedicated, req.DedicatedBoundToLauncher, req.DedicatedIdleTimeoutSeconds, ct);
                startedNow = true;
            }
            if (ds.LoadedModel?.Directory != model.Directory)
            {
                progress?.Invoke($"Loading {model.Manifest.DisplayName}...");
                await supervisor.LoadAsync(dedicated, model, ct);
            }
            return (dedicated, !startedNow, startedNow, true);
        }

        bool keep = req.KeepWorker;
        var id = WorkerSupervisor.ValidateWorkerId(req.WorkerId ?? DefaultWorker);
        var st = await supervisor.GetStatusAsync(id, ct);

        if (st.State is "ready" or "generating" && st.LoadedModel?.Directory == model.Directory) return (id, true, false, true);
        if (st.State is "idle")
        {
            progress?.Invoke($"Loading {model.Reference} into worker '{id}'...");
            await supervisor.LoadAsync(id, model, ct);
            return (id, true, false, true);
        }
        if (req.WorkerId is not null && st.State is not ("stopped" or "error"))
            throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy,
                $"Worker '{id}' is {st.State} with {(st.LoadedModel is null ? "no model" : st.LoadedModel.Id + "@" + st.LoadedModel.Version)}; it can't serve {model.Reference} now.", st);

        if (st.State is "stopped" or "error" && (req.WorkerId is not null || keep))
        {
            progress?.Invoke($"Starting worker '{id}'...");
            await supervisor.StartAsync(id, boundToLauncher: !keep, keep ? storage.Settings.Workers.IdleTimeoutSeconds : 0, ct);
        }
        else
        {
            // A persistent 'default' worker serves another model (or is busy loading): use a private one-shot worker.
            id = $"synth-{Environment.ProcessId}";
            keep = false;
            progress?.Invoke($"Starting one-shot worker '{id}'...");
            await supervisor.StartAsync(id, boundToLauncher: true, 0, ct);
        }
        progress?.Invoke($"Loading {model.Reference}...");
        await supervisor.LoadAsync(id, model, ct);
        return (id, false, true, keep);
    }

    async Task SafeStopAsync(string id)
    {
        try { await supervisor.StopAsync(id, force: false, TimeSpan.FromSeconds(15), CancellationToken.None); }
        catch (NoorVoxException) { await supervisor.StopAsync(id, force: true, TimeSpan.FromSeconds(5), CancellationToken.None); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

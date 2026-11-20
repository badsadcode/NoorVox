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
    public const string DefaultWorker = WorkerLease.DefaultWorker;
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

        var lease = await WorkerLease.AcquireAsync(storage, supervisor, model, new WorkerPolicy
        {
            WorkerId = req.WorkerId, KeepWorker = req.KeepWorker, Dedicated = req.DedicatedWorker,
            DedicatedBoundToLauncher = req.DedicatedBoundToLauncher, DedicatedIdleTimeoutSeconds = req.DedicatedIdleTimeoutSeconds,
        }, progress, ct);
        string workerId = lease.Id;
        SynthesisResult result;
        try
        {
            progress?.Invoke($"Synthesizing {text.Length} chars with {voiceId} on worker '{workerId}'...");
            result = await supervisor.SynthesizeAsync(workerId, new SynthesizeParams(text, voiceId, req.Speed, tempWav), ct);
        }
        catch
        {
            TryDelete(tempWav);
            await lease.DisposeAsync();
            throw;
        }
        await lease.DisposeAsync();
        bool stoppedAfter = lease.Stopped;
        bool reused = lease.Reused, started = lease.Started;

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

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

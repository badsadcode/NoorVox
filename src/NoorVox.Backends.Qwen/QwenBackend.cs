using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Workers;

namespace NoorVox.Backends.Qwen;

/// <summary>
/// Qwen3-TTS via the qwen-tts engine pack (ADR-0002). Inference runs in a Python sidecar started from the engine's
/// private Python; this class only launches it, speaks JSON lines over its stdin/stdout and checks the WAV it writes.
/// The sidecar is in a kill-on-close Job Object owned by this worker, and exits on stdin EOF, so it never outlives the worker.
/// </summary>
public sealed class QwenBackend(Action<string> log) : ITtsBackend, IBackendResources
{
    static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);  // a cold `import torch` from a busy HDD is slow
    readonly SemaphoreSlim _io = new(1, 1);
    Process? _proc;
    JobObject? _job;
    InstalledModel? _model;
    string _version = "faster-qwen3-tts (engine not started)";
    GpuMemoryInfo? _gpu;
    long _requestNo;

    public string BackendId => "qwen3-tts";
    public string BackendVersion => _version;
    public string ExecutionProvider => _model is null ? "" : "CUDA";
    public bool IsLoaded => _model is not null;
    public GpuMemoryInfo? Gpu => _gpu;
    public string? ProviderNote => null;

    public SidecarInfo? Sidecar
    {
        get
        {
            var p = _proc;
            if (p is null) return null;
            try
            {
                if (p.HasExited) return null;
                p.Refresh();
                return new SidecarInfo(p.Id, "Qwen3-TTS engine (Python)", p.WorkingSet64, p.PrivateMemorySize64, DateTimeOffset.UtcNow);
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    public async Task LoadAsync(InstalledModel model, BackendLoadOptions options, CancellationToken ct)
    {
        if (_model is not null) throw new InvalidOperationException("A model is already loaded; recycle the worker to change models.");
        if (options.EngineDirectory is null || options.SidecarScript is null)
            throw new NoorVoxException(ErrorCodes.EngineNotInstalled, ExitCode.ModelMissing, "The qwen-tts engine pack is not installed.");
        var python = Path.Combine(options.EngineDirectory, "python", "python.exe");
        if (!File.Exists(python))
            throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"Engine Python not found: {python}. Reinstall the qwen-tts engine.");

        await StartSidecarAsync(python, options, ct);
        var reply = await CallAsync(new JsonObject { ["op"] = "load", ["modelDir"] = model.Directory, ["warmup"] = true }, ct);
        ReadGpu(reply);
        log($"qwen model loaded: load {reply["loadMs"]} ms, CUDA graph warm-up {reply["warmupMs"]} ms");
        _model = model;
    }

    async Task StartSidecarAsync(string python, BackendLoadOptions options, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(options.SidecarScript)!,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(options.SidecarScript!);
        // Offline by construction: the model is a local folder; nothing may be fetched or reported.
        psi.Environment["HF_HUB_OFFLINE"] = "1";
        psi.Environment["TRANSFORMERS_OFFLINE"] = "1";
        psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["CUDA_MODULE_LOADING"] = "LAZY";
        if (options.CacheDirectory is { } cache)
        {
            psi.Environment["HF_HOME"] = Path.Combine(cache, "hf");
            psi.Environment["TORCHINDUCTOR_CACHE_DIR"] = Path.Combine(cache, "torch");
            psi.Environment["NUMBA_CACHE_DIR"] = Path.Combine(cache, "numba");
        }
        psi.Environment[WorkerSupervisor.TokenEnvVar] = null; // the worker token never reaches Python

        _job = new JobObject();
        _proc = Process.Start(psi) ?? throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"Could not start {python}.");
        if (!_job.Assign(_proc.Handle)) log($"warning: could not put the Qwen engine (PID {_proc.Id}) in the worker's job object");
        log($"qwen engine started: PID {_proc.Id}");
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _proc.StandardError.ReadLineAsync() is { } line)
                    if (line.Length > 0) log("qwen: " + line);
            }
            catch (Exception) { /* process gone */ }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        JsonObject ready;
        try { ready = await ReadMessageAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"The Qwen engine did not start within {StartTimeout.TotalMinutes:0} minutes.");
        }
        if (ready["ok"]?.GetValue<bool>() != true) throw ToException(ready);
        _version = $"faster-qwen3-tts; PyTorch {ready["torch"]} (CUDA {ready["cudaVersion"]}); Python {ready["python"]}";
        if (ready["cuda"]?.GetValue<bool>() != true)
            throw new NoorVoxException(ErrorCodes.GpuRequired, ExitCode.WorkerError, "Qwen3-TTS needs an NVIDIA GPU with CUDA; PyTorch sees none on this PC.");
        log($"qwen engine ready: {_version}; device {ready["device"]}");
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, string outputWavPath, CancellationToken ct)
    {
        if (_model is null) throw new InvalidOperationException("No model loaded.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is empty.");
        var caps = _model.Capabilities;
        if (Math.Abs(request.Speed - 1.0) > 1e-9)
            throw new NoorVoxException(ErrorCodes.Unsupported, ExitCode.InvalidArguments, "Qwen3-TTS has no speed control; ask for a pace in the style instead.");
        if (!string.IsNullOrWhiteSpace(request.Style) && !caps.Style)
            throw new NoorVoxException(ErrorCodes.Unsupported, ExitCode.InvalidArguments, $"{_model.Manifest.DisplayName} does not take style directions.");

        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(0, int.MaxValue);
        var msg = new JsonObject { ["op"] = "synth", ["text"] = request.Text, ["seed"] = seed, ["out"] = Path.GetFullPath(outputWavPath) };
        string language;
        if (caps.Cloning)
        {
            var profile = FindProfile(request.VoiceId);
            msg["mode"] = "clone";
            msg["refAudio"] = profile.ReferencePath;
            msg["refText"] = profile.Transcript;
            language = request.Language ?? profile.Language;
        }
        else
        {
            if (_model.Manifest.BuiltInVoices?.Any(v => v.Id == request.VoiceId) != true)
                throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"'{request.VoiceId}' is not a speaker of {_model.Reference}.");
            msg["mode"] = "custom";
            msg["speaker"] = request.VoiceId;
            if (!string.IsNullOrWhiteSpace(request.Style)) msg["instruct"] = request.Style.Trim();
            language = request.Language ?? "auto";
        }
        if (caps.Languages.Count > 0 && !caps.Languages.Contains(language))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Language '{language}' is not supported. Use one of: {string.Join(", ", caps.Languages)}.");
        msg["language"] = language == "auto" ? "Auto" : char.ToUpperInvariant(language[0]) + language[1..];

        var sw = Stopwatch.StartNew();
        var reply = await CallAsync(msg, ct);
        sw.Stop();
        ReadGpu(reply);
        var info = WavInfo.Read(outputWavPath);
        return new SynthesisResult(outputWavPath, info.SampleRate, info.Channels, info.BitsPerSample, info.Samples, info.DurationMs,
            sw.ElapsedMilliseconds, seed);
    }

    VoiceProfile FindProfile(string voiceId)
    {
        var p = _model!.Profiles?.FirstOrDefault(x => x.VoiceId == voiceId);
        if (p is null && VoiceProfiles.IsValidId(voiceId))
        {
            _model = ModelRegistry.ReadPackage(_model.Directory); // a profile created after this worker loaded the model
            p = _model.Profiles?.FirstOrDefault(x => x.VoiceId == voiceId);
        }
        return p ?? throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"No voice profile '{voiceId}'.", new { voiceId });
    }

    void ReadGpu(JsonObject reply)
    {
        if (reply["vram"] is not JsonObject v) return;
        _gpu = new GpuMemoryInfo(v["device"]?.GetValue<string>() ?? "", v["allocatedBytes"]!.GetValue<long>(), v["reservedBytes"]!.GetValue<long>(),
            v["peakAllocatedBytes"]!.GetValue<long>(), v["deviceFreeBytes"]!.GetValue<long>(), v["deviceTotalBytes"]!.GetValue<long>(),
            "Qwen engine (torch.cuda), sampled after the last load/synthesis", DateTimeOffset.Parse(v["sampledAt"]!.GetValue<string>()));
    }

    async Task<JsonObject> CallAsync(JsonObject request, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _requestNo).ToString();
            request["id"] = id;
            var proc = _proc ?? throw new InvalidOperationException("Engine not started.");
            await proc.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
            JsonObject reply;
            try { reply = await ReadMessageAsync(ct); }
            catch (OperationCanceledException)
            {
                KillSidecar("cancelled"); // a generation can't be interrupted inside the engine
                throw;
            }
            if (reply["id"]?.GetValue<string>() != id) throw new InvalidDataException("Qwen engine reply out of order.");
            if (reply["ok"]?.GetValue<bool>() != true) throw ToException(reply);
            return reply;
        }
        finally { _io.Release(); }
    }

    async Task<JsonObject> ReadMessageAsync(CancellationToken ct)
    {
        var line = await _proc!.StandardOutput.ReadLineAsync(ct);
        if (line is null)
        {
            var exit = _proc.HasExited ? $" (exit code {_proc.ExitCode})" : "";
            throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"The Qwen engine stopped unexpectedly{exit}; see the worker log.");
        }
        return JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("Qwen engine sent a non-object message.");
    }

    static NoorVoxException ToException(JsonObject reply)
    {
        var code = reply["error"]?["code"]?.GetValue<string>() ?? ErrorCodes.SynthesisFailed;
        var message = reply["error"]?["message"]?.GetValue<string>() ?? "Unknown Qwen engine error.";
        if (code == ErrorCodes.GpuOutOfMemory)
            message = "The GPU ran out of memory. Close other GPU-heavy apps (3D, games) or use a 0.6B model. " + message;
        return new NoorVoxException(code, WorkerClient.ExitCodeFor(code), message);
    }

    void KillSidecar(string why)
    {
        var p = _proc;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                log($"stopping qwen engine PID {p.Id} ({why})");
                p.Kill(entireProcessTree: true); // our own child, addressed by its handle
            }
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        var p = _proc;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.StandardInput.Close(); // EOF: the sidecar exits on its own
                    if (!p.WaitForExit(TimeSpan.FromSeconds(10))) KillSidecar("did not exit after stdin closed");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { KillSidecar("dispose"); }
            p.Dispose();
        }
        _proc = null;
        _job?.Dispose(); // kill-on-close: nothing of the engine survives
        _job = null;
        _model = null;
    }
}

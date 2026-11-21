using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Workers;

namespace NoorVox.Backends.Python;

/// <summary>
/// Shared machinery for engines that run in Python (ADR-0002): the worker starts the engine pack's private Python with a
/// sidecar script, speaks JSON lines over its stdin/stdout, and checks the WAV it writes. The sidecar sits in a kill-on-close
/// Job Object owned by the worker and exits on stdin EOF, so it never outlives the worker. Engines only say how to build a
/// synthesis request (<see cref="BuildSynthRequest"/>); everything else — start-up, load stages, GPU numbers, errors,
/// shutdown — is here. Protocol: <c>engines/common/noorvox_sidecar.py</c>.
/// </summary>
public abstract class PythonSidecarBackend(Action<string> log) : ITtsBackend, IBackendResources
{
    static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);  // a cold `import torch` from a busy HDD is slow
    readonly SemaphoreSlim _io = new(1, 1);
    Process? _proc;
    JobObject? _job;
    GpuMemoryInfo? _gpu;
    long _requestNo;
    string _version = "";

    /// <summary>Shown in logs and messages, e.g. "Qwen3-TTS".</summary>
    protected abstract string EngineName { get; }
    /// <summary>The engine pack id the user would install, e.g. <c>qwen-tts</c>.</summary>
    protected abstract string EnginePackId { get; }
    protected Action<string> Log { get; } = log;
    protected InstalledModel? Model { get; private set; }

    public abstract string BackendId { get; }
    public string BackendVersion => _version.Length > 0 ? _version : $"{EngineName} (engine not started)";
    public string ExecutionProvider => Model is null ? "" : "CUDA";
    public bool IsLoaded => Model is not null;
    public GpuMemoryInfo? Gpu => _gpu;
    public string? ProviderNote => null;
    public string? LoadStage { get; private set; }

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
                return new SidecarInfo(p.Id, $"{EngineName} engine (Python)", p.WorkingSet64, p.PrivateMemorySize64, DateTimeOffset.UtcNow);
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>Builds the engine-specific <c>synth</c> message (mode, voice, controls). Validation of controls belongs here too.</summary>
    protected abstract JsonObject BuildSynthRequest(InstalledModel model, SynthesisRequest request, long seed);

    /// <summary>Extra fields for the <c>load</c> message (e.g. which model class to use).</summary>
    protected virtual void AddLoadFields(InstalledModel model, JsonObject load) { }

    public async Task LoadAsync(InstalledModel model, BackendLoadOptions options, CancellationToken ct)
    {
        if (Model is not null) throw new InvalidOperationException("A model is already loaded; recycle the worker to change models.");
        if (options.EngineDirectory is null || options.SidecarScript is null)
            throw new NoorVoxException(ErrorCodes.EngineNotInstalled, ExitCode.ModelMissing, $"The {EnginePackId} engine pack is not installed.");
        var python = Path.Combine(options.EngineDirectory, "python", "python.exe");
        if (!File.Exists(python))
            throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"Engine Python not found: {python}. Reinstall the {EnginePackId} engine.");

        LoadStage = "Starting Python and PyTorch";
        try
        {
            await StartSidecarAsync(python, options, ct);
            LoadStage = "Reading the model from disk";
            var load = new JsonObject { ["op"] = "load", ["modelDir"] = model.Directory, ["warmup"] = true };
            AddLoadFields(model, load);
            var reply = await CallAsync(load, ct);
            ReadGpu(reply);
            Log($"{EngineName} model loaded: load {reply["loadMs"]} ms, warm-up {reply["warmupMs"]} ms");
        }
        finally { LoadStage = null; }
        Model = model;
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
        if (!_job.Assign(_proc.Handle)) Log($"warning: could not put the {EngineName} engine (PID {_proc.Id}) in the worker's job object");
        Log($"{EngineName} engine started: PID {_proc.Id}");
        var proc = _proc;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                    if (line.Length > 0) Log($"{BackendId}: {line}");
            }
            catch (Exception) { /* process gone */ }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        JsonObject ready;
        try { ready = await ReadMessageAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"The {EngineName} engine did not start within {StartTimeout.TotalMinutes:0} minutes.");
        }
        if (ready["ok"]?.GetValue<bool>() != true) throw ToException(ready);
        _version = $"{ready["engine"]}; PyTorch {ready["torch"]} (CUDA {ready["cudaVersion"]}); Python {ready["python"]}";
        if (ready["cuda"]?.GetValue<bool>() != true)
            throw new NoorVoxException(ErrorCodes.GpuRequired, ExitCode.WorkerError, $"{EngineName} needs an NVIDIA GPU with CUDA; PyTorch sees none on this PC.");
        Log($"{EngineName} engine ready: {_version}; device {ready["device"]}");
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, string outputWavPath, CancellationToken ct)
    {
        if (Model is null) throw new InvalidOperationException("No model loaded.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is empty.");
        var seed = request.Seed ?? RandomNumberGenerator.GetInt32(0, int.MaxValue);
        var msg = BuildSynthRequest(Model, request, seed);
        msg["op"] = "synth";
        msg["text"] = request.Text;
        msg["seed"] = seed;
        msg["out"] = Path.GetFullPath(outputWavPath);

        var sw = Stopwatch.StartNew();
        var reply = await CallAsync(msg, ct);
        sw.Stop();
        ReadGpu(reply);
        var info = WavInfo.Read(outputWavPath);
        return new SynthesisResult(outputWavPath, info.SampleRate, info.Channels, info.BitsPerSample, info.Samples, info.DurationMs,
            sw.ElapsedMilliseconds, seed);
    }

    /// <summary>Validates the language against the model's list and returns it (default <c>auto</c>).</summary>
    protected static string CheckLanguage(InstalledModel model, string? requested, string fallback = "auto")
    {
        var language = requested ?? fallback;
        var langs = model.Capabilities.Languages;
        if (langs.Count > 0 && !langs.Contains(language))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Language '{language}' is not supported. Use one of: {string.Join(", ", langs)}.");
        return language;
    }

    protected VoiceProfile FindProfile(string voiceId)
    {
        var p = Model!.Profiles?.FirstOrDefault(x => x.VoiceId == voiceId);
        if (p is null && VoiceProfiles.IsValidId(voiceId))
        {
            Model = ModelRegistry.ReadPackage(Model.Directory); // a profile created after this worker loaded the model
            p = Model.Profiles?.FirstOrDefault(x => x.VoiceId == voiceId);
        }
        return p ?? throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"No voice profile '{voiceId}'.", new { voiceId });
    }

    protected static NoorVoxException Unsupported(string message) => new(ErrorCodes.Unsupported, ExitCode.InvalidArguments, message);

    void ReadGpu(JsonObject reply)
    {
        if (reply["vram"] is not JsonObject v) return;
        _gpu = new GpuMemoryInfo(v["device"]?.GetValue<string>() ?? "", v["allocatedBytes"]!.GetValue<long>(), v["reservedBytes"]!.GetValue<long>(),
            v["peakAllocatedBytes"]!.GetValue<long>(), v["deviceFreeBytes"]!.GetValue<long>(), v["deviceTotalBytes"]!.GetValue<long>(),
            $"{EngineName} engine (torch.cuda), sampled after the last load/synthesis", DateTimeOffset.Parse(v["sampledAt"]!.GetValue<string>()));
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
            if (reply["id"]?.GetValue<string>() != id) throw new InvalidDataException($"{EngineName} engine reply out of order.");
            if (reply["ok"]?.GetValue<bool>() != true) throw ToException(reply);
            return reply;
        }
        finally { _io.Release(); }
    }

    async Task<JsonObject> ReadMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _proc!.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                var exit = _proc.HasExited ? $" (exit code {_proc.ExitCode})" : "";
                throw new NoorVoxException(ErrorCodes.EngineBroken, ExitCode.WorkerError, $"The {EngineName} engine stopped unexpectedly{exit}; see the worker log.");
            }
            var msg = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException($"{EngineName} engine sent a non-object message.");
            if (msg["event"]?.GetValue<string>() == "progress") // stage report while loading; the reply follows
            {
                LoadStage = msg["stage"]?.GetValue<string>() switch
                {
                    "reading" => "Reading the model from disk",
                    "warmup" => "Preparing the GPU",
                    var other => other,
                };
                Log($"{BackendId} load stage: {LoadStage}");
                continue;
            }
            return msg;
        }
    }

    NoorVoxException ToException(JsonObject reply)
    {
        var code = reply["error"]?["code"]?.GetValue<string>() ?? ErrorCodes.SynthesisFailed;
        var message = reply["error"]?["message"]?.GetValue<string>() ?? $"Unknown {EngineName} engine error.";
        if (code == ErrorCodes.GpuOutOfMemory)
            message = "The GPU ran out of memory. Close other GPU-heavy apps (3D, games), stop other engines in the Monitor, or use a smaller model. " + message;
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
                Log($"stopping {EngineName} engine PID {p.Id} ({why})");
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
        Model = null;
        GC.SuppressFinalize(this);
    }
}

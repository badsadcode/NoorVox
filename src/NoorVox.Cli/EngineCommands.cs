using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Engines;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;

namespace NoorVox.Cli;

/// <summary>Engine packs, voice cloning and GPU settings (ADR-0002).</summary>
public static class EngineCommands
{
    static StorageContext Storage() => StorageContext.Load();

    // ---------------------------------------------------------------- engines

    public static Task<CommandResult> List(CommandContext ctx)
    {
        var s = Storage();
        var engines = new EngineRegistry(s).List(ModelCatalog.LoadEmbedded());
        return Task.FromResult(new CommandResult(new { enginesDir = new EngineRegistry(s).Root, engines },
            string.Join(Environment.NewLine, engines.Select(e => $"{e.Id} {e.Version}: {e.State} ({e.DisplayName})"))));
    }

    public static async Task<CommandResult> Install(CommandContext ctx)
    {
        var id = ctx.Positionals.FirstOrDefault()
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Which engine? See: noorvox engines list");
        var s = Storage();
        var catalog = ModelCatalog.LoadEmbedded();
        var engine = catalog.FindEngine(id);
        if (engine is not null)
            ctx.Info($"Installing {engine.DisplayName} into {new EngineRegistry(s).Root} (about {engine.ApproxInstallBytes / 1e9:0.#} GB; downloads cached in {s.CacheDir})");
        var result = await new EngineInstaller(s, catalog).InstallAsync(id, Commands.InstallProgressReporter(ctx), ctx.Ct);
        return new CommandResult(result, result.Status == "installed" ? $"Installed engine {result.Id} {result.Version}." : $"Engine {result.Id} is already installed.",
            result.Warnings);
    }

    public static Task<CommandResult> Remove(CommandContext ctx)
    {
        var id = ctx.Positionals.FirstOrDefault()
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Which engine? See: noorvox engines list");
        var path = new EngineInstaller(Storage(), ModelCatalog.LoadEmbedded()).Remove(id);
        return Task.FromResult(new CommandResult(new { id, removed = path }, $"Removed engine {id} ({path})."));
    }

    // ---------------------------------------------------------------- voice profiles (cloning)

    public static Task<CommandResult> Clone(CommandContext ctx)
    {
        var s = Storage();
        var a = ctx.Args;
        string transcript = a.Has("transcript-file") ? File.ReadAllText(a.Require("transcript-file")) : a.Require("transcript");
        var profile = VoiceProfiles.Create(s.ModelsDir, new CreateProfileRequest(
            a.Require("name"), a.Require("reference"), transcript, a.Require("consent"),
            (a.Get("language") ?? "auto").ToLowerInvariant(), (a.Get("gender") ?? "unknown").ToLowerInvariant()));
        var users = new ModelRegistry(s).List().Where(m => m.Capabilities.Cloning).Select(m => m.Reference).ToList();
        var warnings = users.Count == 0 ? new[] { "No installed model can use cloned voices yet. Install one: noorvox models install qwen3-tts-1.7b-base" } : null;
        return Task.FromResult(new CommandResult(new
        {
            profile.VoiceId, profile.Name, profile.Language, profile.ReferenceDurationMs, profile.ReferenceSha256, profile.Consent,
            path = profile.Directory, usableWith = users,
        }, $"Created voice profile '{profile.VoiceId}' ({profile.ReferenceDurationMs / 1000.0:0.0} s reference).", warnings));
    }

    public const string DefaultDesignSample = "Hello. This is how I sound when I tell a story, from the quiet beginning to the very last word.";

    /// <summary>VoiceDesign → a sample WAV → a <c>designed</c> voice profile (no real person involved, so no consent statement).</summary>
    public static async Task<CommandResult> Design(CommandContext ctx)
    {
        var s = Storage();
        var a = ctx.Args;
        var designer = new ModelRegistry(s).List().FirstOrDefault(m => m.Capabilities.Design)
            ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "The VoiceDesign model is not installed.",
                new { hint = "noorvox models install qwen3-tts-1.7b-voicedesign" });
        var name = a.Require("name");
        var text = a.Get("text") ?? DefaultDesignSample;
        var language = a.Get("language")?.ToLowerInvariant() ?? "auto";
        var temp = Path.Combine(s.TempDir, "synth", $"design-{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        using var sup = new Core.Workers.WorkerSupervisor(s, Commands.WorkerExePath(), "cli");
        var r = await new Core.Narration.NarrationService(s, sup).SynthesizeAsync(new Core.Narration.NarrationRequest
        {
            Text = text, VoiceId = "designed", Model = designer.Reference, Style = a.Require("description"), Language = language,
            Seed = a.Has("seed") ? long.Parse(a.Require("seed"), System.Globalization.CultureInfo.InvariantCulture) : null, OutputPath = temp,
        }, msg => ctx.Progress(msg, force: true), ctx.Ct);
        try
        {
            var p = VoiceProfiles.CreateGenerated(s.ModelsDir, new GeneratedProfileRequest(name, temp, text, "designed", designer.Reference, "designed",
                a.Require("description"), language));
            var users = new ModelRegistry(s).List().Where(m => m.Capabilities.Cloning).Select(m => m.Reference).ToList();
            return new CommandResult(new { p.VoiceId, p.Name, p.Kind, p.Description, p.ReferenceDurationMs, seed = r.Seed, path = p.Directory, usableWith = users },
                $"Designed voice '{p.VoiceId}' ({r.DurationSeconds:0.0} s sample, seed {r.Seed}).",
                users.Count == 0 ? ["Designed voices are spoken by a Qwen Base model: noorvox models install qwen3-tts-1.7b-base"] : null);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    public static Task<CommandResult> RecordingsList(CommandContext ctx)
    {
        var s = Storage();
        var list = RecordingStore.List(s);
        return Task.FromResult(new CommandResult(new
        {
            dir = RecordingStore.Dir(s),
            recordings = list.Select(r => new { r.Id, r.Name, r.Transcript, r.Notes, r.Source, r.DurationMs, r.SampleRate, r.CreatedAt, path = r.WavPath }),
        }, $"{list.Count} recording(s) in {RecordingStore.Dir(s)}."));
    }

    public static Task<CommandResult> ProfileRemove(CommandContext ctx)
    {
        var id = ctx.Args.Require("voice");
        VoiceProfiles.Remove(Storage().ModelsDir, id);
        return Task.FromResult(new CommandResult(new { voiceId = id, removed = true },
            $"Removed voice profile '{id}'. Takes already made with it are untouched."));
    }

    // ---------------------------------------------------------------- GPU

    public static Task<CommandResult> GpuStatus(CommandContext ctx)
    {
        var s = Storage();
        var reg = new EngineRegistry(s);
        var qwen = reg.Find(EngineIds.QwenTts);
        var ort = reg.Find(EngineIds.OrtCuda);
        var data = new
        {
            kokoroProvider = s.Settings.Gpu.KokoroProvider,
            kokoroCudaReady = qwen is not null && ort is not null,
            engines = new { qwenTts = qwen?.Directory, onnxruntimeCuda = ort?.Directory },
            // Measured by the engine's own PyTorch when it was installed; not re-measured here.
            detected = qwen?.Manifest.Verified, detectedAt = qwen?.Manifest.InstalledAt,
            note = qwen is null ? "GPU detection runs through the qwen-tts engine; install it to see what PyTorch detects." : null,
        };
        var msg = $"Kokoro provider: {s.Settings.Gpu.KokoroProvider}" + (qwen?.Manifest.Verified?.TryGetValue("device", out var dev) == true ? $"; GPU seen by the engine: {dev}" : "");
        return Task.FromResult(new CommandResult(data, msg));
    }

    public static Task<CommandResult> GpuSet(CommandContext ctx)
    {
        var s = Storage();
        var provider = ctx.Args.Require("kokoro").ToLowerInvariant();
        if (provider is not ("cpu" or "cuda"))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "--kokoro must be cpu or cuda.");
        var warnings = new List<string>();
        var reg = new EngineRegistry(s);
        if (provider == "cuda" && (reg.Find(EngineIds.OrtCuda) is null || reg.Find(EngineIds.QwenTts) is null))
            warnings.Add("Kokoro will stay on CPU until both engine packs are installed: noorvox engines install onnxruntime-cuda");
        warnings.Add("Running workers keep their current provider; it applies the next time a worker loads Kokoro (noorvox workers unload).");
        s.Settings.Gpu.KokoroProvider = provider;
        s.Save();
        return Task.FromResult(new CommandResult(new { kokoroProvider = provider }, $"Kokoro provider set to {provider}.", warnings));
    }
}

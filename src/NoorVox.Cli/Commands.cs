using System.Diagnostics;
using NoorVox.Core;
using NoorVox.Core.Audio;
using NoorVox.Core.Backends;
using NoorVox.Core.Ipc;
using NoorVox.Core.Models;
using NoorVox.Core.Storage;
using NoorVox.Core.Util;
using NoorVox.Core.Workers;

namespace NoorVox.Cli;

public static class Commands
{
    const string DefaultWorker = "default";

    static StorageContext Storage() => StorageContext.Load();

    static string WorkerExe() =>
        Environment.GetEnvironmentVariable("NOORVOX_WORKER_EXE") is { Length: > 0 } p ? Path.GetFullPath(p)
            : Path.Combine(AppContext.BaseDirectory, "NoorVox.Worker.exe");

    static WorkerSupervisor Supervisor(StorageContext s) => new(s, WorkerExe(), "cli");

    static IProgress<InstallProgress> InstallProgressReporter(CommandContext ctx) => new SyncProgress<InstallProgress>(p =>
    {
        string text = p.Stage switch
        {
            "downloading" when p.Total > 0 => $"{p.Item}: downloading {p.File} {p.Done * 100 / p.Total,3}% ({p.Done / 1048576.0:0.0}/{p.Total / 1048576.0:0.0} MB)",
            "hashing" when p.Total > 0 => $"{p.Item}: hashing {p.File} {p.Done * 100 / p.Total,3}%",
            _ => $"{p.Item}: {p.Stage} {p.File}",
        };
        ctx.Progress(text, force: p.Done == p.Total && p.Total > 0);
    });

    /// <summary>Progress&lt;T&gt; posts to the thread pool (out of order); this one reports synchronously.</summary>
    sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ---------------------------------------------------------------- general

    public static Task<CommandResult> Version(CommandContext ctx) => Task.FromResult(new CommandResult(new
    {
        version = NoorVoxInfo.Version,
        schemaVersion = NoorVoxInfo.SchemaVersion,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        executable = Environment.ProcessPath,
    }, $"NoorVox {NoorVoxInfo.Version}"));

    public static async Task<CommandResult> Doctor(CommandContext ctx)
    {
        var s = Storage();
        var issues = new List<string>();
        var registry = new ModelRegistry(s);
        var catalog = ModelCatalog.LoadEmbedded();
        var models = registry.List();

        if (models.Count == 0) issues.Add("No voice model installed. Fix: noorvox models install kokoro-82m");
        var modelReports = models.Select(m =>
        {
            var v = ModelRegistry.Verify(m.Directory, m.Manifest.Files.Concat(m.VoicePacks.SelectMany(p => p.Files)), full: false);
            if (!v.Ok) issues.Add($"{m.Reference}: {v.Problems.Count} file problem(s). Fix: noorvox models install {m.Id}@{m.Version}");
            if (m.Voices.Count == 0) issues.Add($"{m.Reference}: no voices installed. Fix: noorvox voices packs --model {m.Reference}");
            return new { model = m.Reference, path = m.Directory, voices = m.Voices.Count, voicePacks = m.VoicePacks.Select(p => p.Id), quickVerify = v.Ok ? "ok" : "problems" };
        }).ToList();

        var exe = WorkerExe();
        if (!File.Exists(exe)) issues.Add($"Worker executable missing: {exe}");

        using var sup = Supervisor(s);
        var workers = await sup.ListAsync(ctx.Ct);
        foreach (var w in workers.Where(w => w.State is "error" or "unresponsive"))
            issues.Add($"Worker '{w.WorkerId}' is {w.State}. Fix: noorvox workers stop --worker {w.WorkerId} --force");

        var data = new
        {
            healthy = issues.Count == 0,
            issues,
            version = NoorVoxInfo.Version,
            home = new { path = s.Home, mode = s.Mode.ToString().ToLowerInvariant(), settingsFile = s.SettingsPath, settingsFileExists = File.Exists(s.SettingsPath) },
            storage = s.Report(),
            backends = new[] { new { id = "kokoro-onnx", executionProviders = new[] { "CPU" }, gpu = "not supported yet (see ADR-0001)" } },
            catalogVersion = catalog.CatalogVersion,
            models = modelReports,
            workerExecutable = new { path = exe, exists = File.Exists(exe) },
            workers = workers.Select(w => new { w.WorkerId, w.State, w.Pid }),
            network = "not required for synthesis; only models/voices install & update download",
        };
        return new CommandResult(data, issues.Count == 0 ? "NoorVox is healthy." : $"NoorVox found {issues.Count} issue(s).");
    }

    // ---------------------------------------------------------------- storage

    public static Task<CommandResult> StorageShow(CommandContext ctx)
    {
        var s = Storage();
        return Task.FromResult(new CommandResult(new
        {
            home = s.Home,
            mode = s.Mode.ToString().ToLowerInvariant(),
            settingsFile = s.SettingsPath,
            paths = s.Report(),
            note = "C: is allowed. Any path can point to any drive. Windows and system components may still write to OS-managed locations.",
        }, "Storage locations."));
    }

    public static Task<CommandResult> StorageSet(CommandContext ctx)
    {
        var s = Storage();
        var changed = new List<object>();
        foreach (var kind in Enum.GetValues<StorageKind>())
        {
            var key = kind.ToString().ToLowerInvariant();
            if (!ctx.Args.Has(key)) continue;
            var value = ctx.Args.Get(key);
            var r = s.SetPath(kind, value is null or "default" ? null : value);
            changed.Add(new { kind = key, path = r.Path, source = r.Source });
        }
        if (changed.Count == 0)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Nothing to set. Example: noorvox storage set --models E:\\AI\\NoorVox\\Models");
        return Task.FromResult(new CommandResult(new { settingsFile = s.SettingsPath, changed, paths = s.Report() },
            $"Updated {changed.Count} storage path(s). Existing files were not moved."));
    }

    // ---------------------------------------------------------------- models

    public static Task<CommandResult> ModelsList(CommandContext ctx)
    {
        var s = Storage();
        var installed = new ModelRegistry(s).List();
        var catalog = ModelCatalog.LoadEmbedded();
        var data = new
        {
            modelsDir = s.ModelsDir,
            installed = installed.Select(m => new
            {
                id = m.Id, version = m.Version, reference = m.Reference, displayName = m.Manifest.DisplayName,
                backend = m.Manifest.Backend, license = m.Manifest.License, source = m.Manifest.Source, origin = m.Manifest.Origin,
                path = m.Directory, modelSha256 = m.ModelSha256, voices = m.Voices.Count,
                voicePacks = m.VoicePacks.Select(p => new { p.Id, p.Version }),
                isDefault = s.Settings.DefaultModel is { } d && (d == m.Id || d == m.Reference),
            }),
            catalog = catalog.Models.Select(c => new
            {
                id = c.Id, version = c.Version, displayName = c.DisplayName, description = c.Description, recommended = c.Recommended,
                license = c.License, downloadBytes = c.Downloads.Sum(d => d.Size)
                    + c.DefaultVoicePacks.Select(catalog.FindVoicePack).Where(p => p is not null).Sum(p => p!.Downloads.Sum(d => d.Size)),
                installed = installed.Any(m => m.Id == c.Id && m.Version == c.Version),
                updateAvailable = installed.Any(m => m.Id == c.Id) && !installed.Any(m => m.Id == c.Id && ModelRef.ParseVersion(m.Version) >= ModelRef.ParseVersion(c.Version)),
            }),
            catalogVersion = catalog.CatalogVersion,
        };
        return Task.FromResult(new CommandResult(data, $"{installed.Count} model(s) installed."));
    }

    public static async Task<CommandResult> ModelsInstall(CommandContext ctx)
    {
        var reference = ctx.Positionals.FirstOrDefault()
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Which model? Example: noorvox models install kokoro-82m");
        var s = Storage();
        var catalog = ModelCatalog.LoadEmbedded();
        var model = catalog.FindModel(reference) ?? throw new NoorVoxException(ErrorCodes.UnknownModel, ExitCode.ModelMissing,
            $"'{reference}' is not in the catalog.", new { available = catalog.Models.Select(m => $"{m.Id}@{m.Version}") });
        ctx.Info($"Installing {model.DisplayName} ({model.License}) into {s.ModelsDir} (staging in {s.CacheDir})");
        var result = await new ModelInstaller(s, catalog).InstallModelAsync(model, !ctx.Args.Has("no-voices"), InstallProgressReporter(ctx), ctx.Ct);
        return new CommandResult(result, result.Status == "installed" ? $"Installed {model.Id}@{model.Version}." : $"{model.Id}@{model.Version} is already installed.");
    }

    public static async Task<CommandResult> ModelsUpdate(CommandContext ctx)
    {
        var s = Storage();
        var catalog = ModelCatalog.LoadEmbedded();
        var installed = new ModelRegistry(s).List();
        var plans = installed.GroupBy(m => m.Id).Select(g =>
        {
            var newestInstalled = g.Max(m => ModelRef.ParseVersion(m.Version))!;
            var latest = catalog.FindModel(g.Key);
            return new { id = g.Key, installed = newestInstalled.ToString(), latest = latest?.Version, catalogModel = latest,
                updateAvailable = latest is not null && ModelRef.ParseVersion(latest.Version) > newestInstalled };
        }).ToList();

        var results = new List<InstallResult>();
        if (!ctx.Args.Has("check"))
        {
            foreach (var p in plans)
            {
                // Installing the latest also repairs/installs its default voice packs; older versions stay (projects pin them).
                if (p.catalogModel is null) continue;
                results.Add(await new ModelInstaller(s, catalog).InstallModelAsync(p.catalogModel, true, InstallProgressReporter(ctx), ctx.Ct));
            }
        }
        var available = plans.Count(p => p.updateAvailable);
        return new CommandResult(new
        {
            catalogVersion = catalog.CatalogVersion,
            models = plans.Select(p => new { p.id, p.installed, p.latest, p.updateAvailable }),
            results,
        }, ctx.Args.Has("check")
            ? (available == 0 ? "All installed models are up to date." : $"{available} update(s) available. Run: noorvox models update")
            : (results.Any(r => r.Status == "installed" || (r.VoicePacks?.Any(v => v.Status == "installed") ?? false)) ? "Updates installed." : "Everything was already up to date."));
    }

    public static async Task<CommandResult> ModelsImport(CommandContext ctx)
    {
        var from = ctx.Args.Require("from");
        var s = Storage();
        ctx.Info($"Importing from {Path.GetFullPath(from)} into {s.ModelsDir} (verifying sha256 against the catalog)");
        var result = await new ModelInstaller(s, ModelCatalog.LoadEmbedded()).ImportAsync(from, InstallProgressReporter(ctx), ctx.Ct);
        return new CommandResult(result, $"Imported {result.Id}@{result.Version} with {result.VoicePacks?.Count ?? 0} voice pack(s).", result.Warnings);
    }

    public static Task<CommandResult> ModelsVerify(CommandContext ctx) => Task.Run(() =>
    {
        var s = Storage();
        var registry = new ModelRegistry(s);
        var targets = ctx.Positionals.FirstOrDefault() is { } r ? [registry.Resolve(r)] : registry.List();
        if (targets.Count == 0) throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "No models installed.");
        bool full = !ctx.Args.Has("quick");
        var reports = targets.Select(m => new
        {
            model = m.Reference,
            path = m.Directory,
            modelFiles = ModelRegistry.Verify(m.Directory, m.Manifest.Files, full, f => ctx.Progress($"{m.Reference}: verifying {f}")),
            voicePacks = m.VoicePacks.Select(p => new { pack = p.Id, result = ModelRegistry.Verify(m.Directory, p.Files, full, f => ctx.Progress($"{p.Id}: verifying {f}")) }).ToList(),
        }).ToList();
        bool ok = reports.All(x => x.modelFiles.Ok && x.voicePacks.All(v => v.result.Ok));
        if (!ok)
            throw new NoorVoxException(ErrorCodes.VerificationFailed, ExitCode.DownloadFailed, "Verification found problems. Reinstall with `noorvox models install <id>`.", reports);
        return new CommandResult(reports, $"All files verified ({(full ? "sha256" : "size only")}).");
    }, ctx.Ct);

    // ---------------------------------------------------------------- voices

    public static Task<CommandResult> VoicesList(CommandContext ctx)
    {
        var s = Storage();
        var model = new ModelRegistry(s).Resolve(ctx.Args.Get("model"));
        var voices = KokoroVoiceNames.List(model);
        if (ctx.Args.Get("language") is { } lang) voices = voices.Where(v => v.Language.Equals(lang, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(new CommandResult(new
        {
            model = model.Reference,
            defaultVoice = s.Settings.DefaultVoice ?? model.Manifest.DefaultVoice,
            count = voices.Count,
            voices,
        }, $"{voices.Count} voice(s) for {model.Reference}."));
    }

    public static Task<CommandResult> VoicesPacks(CommandContext ctx)
    {
        var s = Storage();
        var model = new ModelRegistry(s).Resolve(ctx.Args.Get("model"));
        var catalog = ModelCatalog.LoadEmbedded();
        var packs = catalog.VoicePacksFor(model.Id, model.Version).Select(p => new
        {
            id = p.Id, version = p.Version, displayName = p.DisplayName, description = p.Description, license = p.License,
            voices = p.Files.Count, downloadBytes = p.Downloads.Sum(d => d.Size),
            installed = model.VoicePacks.Any(i => i.Id == p.Id && i.Version == p.Version),
        }).ToList();
        return Task.FromResult(new CommandResult(new { model = model.Reference, packs }, $"{packs.Count} voice pack(s) available for {model.Reference}."));
    }

    public static async Task<CommandResult> VoicesInstall(CommandContext ctx)
    {
        var reference = ctx.Positionals.FirstOrDefault()
            ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Which voice pack? See: noorvox voices packs");
        var s = Storage();
        var catalog = ModelCatalog.LoadEmbedded();
        var pack = catalog.FindVoicePack(reference) ?? throw new NoorVoxException(ErrorCodes.UnknownModel, ExitCode.ModelMissing,
            $"Voice pack '{reference}' is not in the catalog.", new { available = catalog.VoicePacks.Select(p => p.Id) });
        var model = new ModelRegistry(s).Resolve(ctx.Args.Get("model") ?? pack.ModelId);
        var result = await new ModelInstaller(s, catalog).InstallVoicePackAsync(pack, model, InstallProgressReporter(ctx), ctx.Ct);
        return new CommandResult(result, result.Status == "installed" ? $"Installed voice pack {pack.Id}." : $"Voice pack {pack.Id} is already installed.");
    }

    // ---------------------------------------------------------------- workers

    static string WorkerId(CommandContext ctx) => WorkerSupervisor.ValidateWorkerId(ctx.Args.Get("worker") ?? DefaultWorker);

    public static async Task<CommandResult> WorkersList(CommandContext ctx)
    {
        var s = Storage();
        using var sup = Supervisor(s);
        var list = await sup.ListAsync(ctx.Ct);
        return new CommandResult(new { workers = list, scope = "NoorVox-managed workers only (registry: " + s.WorkerRegistryDir + ")" },
            list.Count == 0 ? "No NoorVox workers are running." : $"{list.Count} NoorVox worker(s).");
    }

    public static async Task<CommandResult> WorkersStatus(CommandContext ctx)
    {
        using var sup = Supervisor(Storage());
        var st = await sup.GetStatusAsync(WorkerId(ctx), ctx.Ct);
        return new CommandResult(st, $"Worker '{st.WorkerId}' is {st.State}.");
    }

    public static async Task<CommandResult> WorkersStart(CommandContext ctx)
    {
        var s = Storage();
        var id = WorkerId(ctx);
        InstalledModel? model = ctx.Args.Has("model") ? new ModelRegistry(s).Resolve(ctx.Args.Get("model")) : null;
        int idle = ctx.Args.GetInt("idle-timeout", s.Settings.Workers.IdleTimeoutSeconds);
        using var sup = Supervisor(s);
        ctx.Progress($"Starting worker '{id}'...", force: true);
        var st = await sup.StartAsync(id, boundToLauncher: false, idle, ctx.Ct);
        if (model is not null)
        {
            ctx.Progress($"Loading {model.Reference}...", force: true);
            st = await sup.LoadAsync(id, model, ctx.Ct);
        }
        var idleNote = idle > 0 ? $" It exits after {idle} s idle." : " No idle timeout: stop it with `noorvox workers stop`.";
        return new CommandResult(st, $"Worker '{id}' started (PID {st.Pid}, {st.State}).{idleNote}");
    }

    public static async Task<CommandResult> WorkersLoad(CommandContext ctx)
    {
        var s = Storage();
        var model = new ModelRegistry(s).Resolve(ctx.Args.Get("model"));
        using var sup = Supervisor(s);
        var st = await sup.LoadAsync(WorkerId(ctx), model, ctx.Ct);
        return new CommandResult(st, $"Loaded {model.Reference} into worker '{st.WorkerId}' in {st.LoadedModel?.LoadMs} ms.");
    }

    public static async Task<CommandResult> WorkersUnload(CommandContext ctx)
    {
        using var sup = Supervisor(Storage());
        var id = WorkerId(ctx);
        var st = await sup.RecycleAsync(id, ctx.Ct);
        return new CommandResult(new { recycled = true, status = st },
            $"Unloaded by recycling worker '{id}': new PID {st.Pid}, no model loaded (in-process unload cannot free all memory; see ADR-0001).");
    }

    public static async Task<CommandResult> WorkersStop(CommandContext ctx)
    {
        using var sup = Supervisor(Storage());
        bool force = ctx.Args.Has("force");
        var timeout = TimeSpan.FromSeconds(ctx.Args.GetInt("timeout", 30));
        if (ctx.Args.Has("all"))
        {
            var reports = await sup.StopAllAsync(force, timeout, ctx.Ct);
            return new CommandResult(new { stopped = reports }, $"Stopped {reports.Count(r => r.WasRunning)} worker(s).");
        }
        if (!ctx.Args.Has("worker"))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Specify --worker ID or --all.");
        var r = await sup.StopAsync(WorkerId(ctx), force, timeout, ctx.Ct);
        return new CommandResult(r, r.WasRunning ? $"Worker '{r.WorkerId}' stopped ({r.Method})." : r.Note ?? $"Worker '{r.WorkerId}' was not running.");
    }

    // ---------------------------------------------------------------- synth

    public static async Task<CommandResult> Synth(CommandContext ctx)
    {
        var total = Stopwatch.StartNew();
        var s = Storage();
        var a = ctx.Args;

        int sources = (a.Has("text") ? 1 : 0) + (a.Has("text-file") ? 1 : 0) + (a.Has("stdin") ? 1 : 0);
        if (sources != 1)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Give exactly one of --text, --text-file or --stdin.");
        string text = a.Has("text") ? a.Get("text") ?? ""
            : a.Has("text-file") ? await ReadTextFile(a.Require("text-file"), ctx.Ct)
            : await Console.In.ReadToEndAsync(ctx.Ct);
        text = text.Trim();
        if (text.Length == 0) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is empty.");

        var model = new ModelRegistry(s).Resolve(a.Get("model"));
        var voiceId = a.Get("voice") ?? s.Settings.DefaultVoice ?? model.Manifest.DefaultVoice ?? model.Voices.FirstOrDefault().VoiceId
            ?? throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"{model.Reference} has no voices installed.");
        if (!model.Voices.Any(v => v.VoiceId == voiceId))
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{voiceId}' is not installed for {model.Reference}.",
                new { voiceId, hint = $"noorvox voices list --model {model.Reference}" });
        double speed = a.GetDouble("speed", 1.0);
        if (speed is < 0.5 or > 2.0)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "--speed must be between 0.5 and 2.0 (Kokoro's supported range).");

        var outPath = Path.GetFullPath(a.Get("out") ?? Path.Combine(s.Resolve(StorageKind.Exports).Path, $"noorvox_{DateTime.Now:yyyyMMdd_HHmmss}.wav"));
        if (!outPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "--out must end in .wav (WAV is the v1 output format).");
        if (File.Exists(outPath) && !a.Has("overwrite"))
            throw new NoorVoxException(ErrorCodes.OutputExists, ExitCode.IoError, $"{outPath} already exists. Pass --overwrite to replace it.", new { outputPath = outPath });

        var tempDir = Path.Combine(s.TempDir, "synth");
        Directory.CreateDirectory(tempDir);
        var tempWav = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");

        using var sup = Supervisor(s);
        var (workerId, reused, started, keep) = await AcquireWorkerAsync(ctx, sup, s, model);
        SynthesisResult result;
        try
        {
            ctx.Progress($"Synthesizing {text.Length} chars with {voiceId} on worker '{workerId}'...", force: true);
            result = await sup.SynthesizeAsync(workerId, new SynthesizeParams(text, voiceId, speed, tempWav), ctx.Ct);
        }
        catch
        {
            TryDelete(tempWav);
            if (started && !keep) await SafeStop(sup, workerId);
            throw;
        }

        bool stoppedAfter = false;
        if (started && !keep)
        {
            await SafeStop(sup, workerId);
            stoppedAfter = true;
        }

        // Validate before publishing: only a real, readable PCM WAV is moved into place.
        WavInfo info;
        try { info = WavInfo.Read(tempWav); }
        catch (InvalidDataException ex)
        {
            TryDelete(tempWav);
            throw new NoorVoxException(ErrorCodes.SynthesisFailed, ExitCode.SynthesisFailed, $"Worker output failed validation: {ex.Message}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.Move(tempWav, outPath, overwrite: a.Has("overwrite"));
        total.Stop();

        var status = await sup.GetStatusAsync(workerId, CancellationToken.None);
        var data = new Dictionary<string, object?>
        {
            ["outputPath"] = outPath,
            ["durationMs"] = info.DurationMs,
            ["durationSeconds"] = Math.Round(info.DurationSeconds, 3),
            ["sampleRate"] = info.SampleRate,
            ["channels"] = info.Channels,
            ["bitsPerSample"] = info.BitsPerSample,
            ["samples"] = info.Samples,
            ["bytes"] = new FileInfo(outPath).Length,
            ["text"] = new { chars = text.Length, sha256 = FileUtil.Sha256Text(text) },
            ["voice"] = voiceId,
            ["speed"] = speed,
            ["model"] = new { id = model.Id, version = model.Version, modelSha256 = model.ModelSha256, license = model.Manifest.License },
            ["backend"] = new { id = "kokoro-onnx", executionProvider = "CPU" },
            ["worker"] = new { id = workerId, reused, startedForRequest = started, stoppedAfter, stateAfter = status.State },
            ["timings"] = new { synthesisMs = result.ElapsedMs, totalMs = total.ElapsedMilliseconds, realTimeFactor = Math.Round(result.ElapsedMs / Math.Max(1.0, info.DurationMs), 3) },
            ["createdAt"] = DateTimeOffset.Now,
            ["generator"] = $"NoorVox {NoorVoxInfo.Version}",
        };
        if (a.Has("manifest"))
        {
            var manifestPath = Path.ChangeExtension(outPath, ".noorvox.json");
            FileUtil.WriteAllTextAtomic(manifestPath, Json.Serialize(data));
            data["manifestPath"] = manifestPath;
        }
        return new CommandResult(data, $"Wrote {outPath} ({info.DurationSeconds:0.00} s).");
    }

    /// <summary>
    /// Worker policy for one-shot synthesis: reuse a running persistent worker when compatible; otherwise start one.
    /// Without --keep-worker the new worker is bound to this CLI process (OS kills it if we die) and stopped afterwards.
    /// </summary>
    static async Task<(string Id, bool Reused, bool Started, bool Keep)> AcquireWorkerAsync(CommandContext ctx, WorkerSupervisor sup, StorageContext s, InstalledModel model)
    {
        bool keep = ctx.Args.Has("keep-worker");
        var requested = ctx.Args.Get("worker");
        var id = WorkerSupervisor.ValidateWorkerId(requested ?? DefaultWorker);
        var st = await sup.GetStatusAsync(id, ctx.Ct);

        if (st.State is "ready" or "generating" && st.LoadedModel?.Directory == model.Directory) return (id, true, false, true);
        if (st.State is "idle")
        {
            ctx.Progress($"Loading {model.Reference} into worker '{id}'...", force: true);
            await sup.LoadAsync(id, model, ctx.Ct);
            return (id, true, false, true);
        }
        if (requested is not null && st.State is not ("stopped" or "error"))
            throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy,
                $"Worker '{id}' is {st.State} with {(st.LoadedModel is null ? "no model" : st.LoadedModel.Id + "@" + st.LoadedModel.Version)}; it can't serve {model.Reference} now.", st);

        if (st.State is "stopped" or "error" && (requested is not null || keep))
        {
            ctx.Progress($"Starting worker '{id}'...", force: true);
            await sup.StartAsync(id, boundToLauncher: !keep, keep ? s.Settings.Workers.IdleTimeoutSeconds : 0, ctx.Ct);
        }
        else
        {
            // A persistent 'default' worker exists but serves another model (or is busy loading): use a private one-shot worker.
            id = $"synth-{Environment.ProcessId}";
            keep = false; // a private one-shot worker is always cleaned up
            ctx.Progress($"Starting one-shot worker '{id}'...", force: true);
            await sup.StartAsync(id, boundToLauncher: true, 0, ctx.Ct);
        }
        ctx.Progress($"Loading {model.Reference}...", force: true);
        await sup.LoadAsync(id, model, ctx.Ct);
        return (id, false, true, keep);
    }

    static async Task SafeStop(WorkerSupervisor sup, string id)
    {
        try { await sup.StopAsync(id, force: false, TimeSpan.FromSeconds(15), CancellationToken.None); }
        catch (NoorVoxException) { await sup.StopAsync(id, force: true, TimeSpan.FromSeconds(5), CancellationToken.None); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    static async Task<string> ReadTextFile(string path, CancellationToken ct)
    {
        try { return await File.ReadAllTextAsync(Path.GetFullPath(path), ct); }
        catch (FileNotFoundException)
        {
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Text file not found: {Path.GetFullPath(path)}");
        }
    }
}

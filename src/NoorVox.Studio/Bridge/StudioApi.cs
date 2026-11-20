using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Engines;
using NoorVox.Core.Models;
using NoorVox.Core.Narration;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Studio.Bridge;

/// <summary>
/// Every operation the UI may perform. Nothing else is reachable from JavaScript: no arbitrary paths, processes or URLs.
/// Paths written to come from storage settings or native dialogs; paths revealed must lie inside NoorVox storage.
/// </summary>
public sealed class StudioApi(MainWindow window, JobObject job, BridgeHost bridge, string workerExe)
{
    public ProjectsApi Projects { get; } = new(window, job, bridge, workerExe);

    public const string StudioWorker = "studio";
    static readonly string[] Themes = ["nocturne", "daylight", "tape", "thalassa", "graphite", "lavender"];
    static readonly string[] UrlHosts = ["huggingface.co", "github.com"];

    static StorageContext S() => StorageContext.Load();
    WorkerSupervisor Sup(StorageContext s) => new(s, workerExe, "studio", job);

    public async Task<object?> InvokeAsync(string method, JsonElement? p, CancellationToken ct) => method switch
    {
        _ when method.StartsWith("projects.", StringComparison.Ordinal) => await Projects.InvokeAsync(method, p, ct),
        "app.info" => AppInfo(),
        "storage.get" => StorageGet(),
        "storage.choose" => await StorageChoose(Arg<KindArgs>(p)),
        "storage.reset" => StorageReset(Arg<KindArgs>(p)),
        "storage.reveal" => StorageReveal(Arg<KindArgs>(p)),
        "models.overview" => ModelsOverview(),
        "models.install" => await ModelsInstall(Arg<IdArgs>(p), ct),
        "models.verify" => await ModelsVerify(Arg<IdArgs>(p), ct),
        "voices.list" => VoicesList(OptArg<ModelArgs>(p)?.Model),
        "voices.chooseReference" => await ChooseReference(),
        "voices.clone" => VoiceClone(Arg<CloneArgs>(p)),
        "voices.removeProfile" => VoiceRemoveProfile(Arg<IdArgs>(p)),
        "engines.list" => new { engines = new EngineRegistry(S()).List(ModelCatalog.LoadEmbedded()) },
        "engines.install" => await EngineInstall(Arg<IdArgs>(p), ct),
        "voices.installPack" => await VoicePackInstall(Arg<IdArgs>(p), ct),
        "voices.blend" => VoiceBlend(Arg<BlendArgs>(p)),
        "workers.list" => await WorkersList(ct),
        "workers.start" => await WorkersStart(ct),
        "workers.unload" => await WorkersUnload(Arg<WorkerArgs>(p), ct),
        "workers.stop" => await WorkersStop(Arg<WorkerStopArgs>(p), ct),
        "workers.stopAll" => await WorkersStopAll(Arg<ForceArgs>(p), ct),
        "narration.generate" => await Generate(Arg<GenerateArgs>(p), ct),
        "narration.export" => await Export(Arg<ExportArgs>(p)),
        "narration.reveal" => RevealFile(Arg<PathArgs>(p)),
        "settings.set" => SettingsSet(Arg<SettingsArgs>(p)),
        "storage.audit" => await Task.Run(() => (object)StorageAudit.Run(S()), ct),
        "diagnostics.bundle" => await Bundle(ct),
        "shell.openUrl" => OpenUrl(Arg<UrlArgs>(p)),
        _ => throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown bridge method '{method}'."),
    };

    // ------------------------------------------------------------ argument records

    public sealed record KindArgs(string Kind);
    public sealed record IdArgs(string Id);
    public sealed record WorkerArgs(string WorkerId);
    public sealed record WorkerStopArgs(string WorkerId, bool Force);
    public sealed record ForceArgs(bool Force);
    public sealed record GenerateArgs(string Text, string Voice, double Speed, string? Model, string? Style, long? Seed, string? Language);
    public sealed record ModelArgs(string? Model);
    public sealed record CloneArgs(string Name, string ReferenceToken, string Transcript, string Consent, string? Language, string? Gender);
    public sealed record ExportArgs(string PreviewPath, string? SuggestedName);
    public sealed record PathArgs(string Path);
    public sealed record SettingsArgs(string? Theme, bool? StopOnExit, int? IdleTimeoutSeconds, string? DefaultVoice, string? KokoroProvider);
    public sealed record UrlArgs(string Url);
    public sealed record BlendArgs(string Name, List<BlendPart> Parts);

    static T? OptArg<T>(JsonElement? p) where T : class => p is { ValueKind: JsonValueKind.Object } e ? e.Deserialize<T>(Json.Compact) : null;

    static T Arg<T>(JsonElement? p) where T : class =>
        (p is { ValueKind: JsonValueKind.Object } e ? e.Deserialize<T>(Json.Compact) : null)
        ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Missing or invalid parameters ({typeof(T).Name}).");

    static StorageKind ParseKind(string kind) =>
        Enum.TryParse<StorageKind>(kind, ignoreCase: true, out var k) ? k
            : throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown storage kind '{kind}'.");

    static T OnUi<T>(Func<T> f) => Application.Current.Dispatcher.Invoke(f);

    // ------------------------------------------------------------ app / storage / settings

    object AppInfo()
    {
        var s = S();
        var models = new ModelRegistry(s).List();
        return new
        {
            version = NoorVoxInfo.Version,
            processorCount = Environment.ProcessorCount,
            firstRun = !File.Exists(s.SettingsPath) || models.Count == 0,
            hasModel = models.Count > 0,
            settings = new
            {
                theme = s.Settings.Studio.Theme,
                stopOnExit = s.Settings.Workers.StopOnExit,
                idleTimeoutSeconds = s.Settings.Workers.IdleTimeoutSeconds,
                defaultVoice = s.Settings.DefaultVoice,
                kokoroProvider = s.Settings.Gpu.KokoroProvider,
            },
            home = new { path = s.Home, mode = s.Mode.ToString().ToLowerInvariant(), settingsFile = s.SettingsPath },
            workerExecutable = new { path = workerExe, exists = File.Exists(workerExe) },
        };
    }

    static object StorageGet()
    {
        var s = S();
        return new { home = s.Home, settingsFile = s.SettingsPath, paths = s.Report() };
    }

    async Task<object?> StorageChoose(KindArgs a)
    {
        var kind = ParseKind(a.Kind);
        var s = S();
        var current = s.Resolve(kind).Path;
        var chosen = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"Choose the NoorVox {kind.ToString().ToLowerInvariant()} folder",
                InitialDirectory = Directory.Exists(current) ? current : Path.GetPathRoot(current) ?? "",
            };
            return dlg.ShowDialog(window) == true ? dlg.FolderName : null;
        });
        if (chosen is null) return new { cancelled = true };
        var r = s.SetPath(kind, chosen);
        if (kind == StorageKind.Temp) await Application.Current.Dispatcher.InvokeAsync(() => window.RemapPreviews(S()));
        return new { cancelled = false, kind = a.Kind, path = r.Path, note = MoveNote(kind) };
    }

    static string? MoveNote(StorageKind kind) => kind switch
    {
        StorageKind.Models => "Existing models were not moved. Install or import them in the new location.",
        StorageKind.WebView => "Takes effect the next time NoorVox Studio starts.",
        _ => "Existing files were not moved.",
    };

    object StorageReset(KindArgs a)
    {
        var kind = ParseKind(a.Kind);
        var r = S().SetPath(kind, null);
        if (kind == StorageKind.Temp) Application.Current.Dispatcher.Invoke(() => window.RemapPreviews(S()));
        return new { kind = a.Kind, path = r.Path, source = r.Source };
    }

    static object StorageReveal(KindArgs a)
    {
        var path = S().Resolve(ParseKind(a.Kind)).Path;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        return new { path };
    }

    object SettingsSet(SettingsArgs a)
    {
        var s = S();
        if (a.Theme is not null)
        {
            if (!Themes.Contains(a.Theme)) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown theme '{a.Theme}'.");
            s.Settings.Studio.Theme = a.Theme;
            Application.Current.Dispatcher.Invoke(() => window.ApplyTheme(a.Theme));
        }
        if (a.StopOnExit is { } stop) s.Settings.Workers.StopOnExit = stop;
        if (a.IdleTimeoutSeconds is { } idle)
        {
            if (idle is < 0 or > 86_400) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Idle timeout must be 0–86400 seconds.");
            s.Settings.Workers.IdleTimeoutSeconds = idle;
        }
        if (a.DefaultVoice is not null) s.Settings.DefaultVoice = a.DefaultVoice.Length == 0 ? null : a.DefaultVoice;
        bool providerChanged = false;
        if (a.KokoroProvider is not null)
        {
            if (a.KokoroProvider is not ("cpu" or "cuda"))
                throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Kokoro provider must be cpu or cuda.");
            providerChanged = s.Settings.Gpu.KokoroProvider != a.KokoroProvider;
            s.Settings.Gpu.KokoroProvider = a.KokoroProvider;
        }
        s.Save();
        if (providerChanged) _ = RecycleIfKokoroAsync(); // the studio worker picks the provider when it loads Kokoro
        return AppInfo();
    }

    async Task<object?> Bundle(CancellationToken ct)
    {
        var s = S();
        var exports = s.Resolve(StorageKind.Exports).Path;
        Directory.CreateDirectory(exports);
        var target = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save support bundle", InitialDirectory = exports, FileName = $"noorvox-support-{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                DefaultExt = ".zip", Filter = "Zip archive (*.zip)|*.zip", OverwritePrompt = true,
            };
            return dlg.ShowDialog(window) == true ? dlg.FileName : null;
        });
        if (target is null) return new { cancelled = true };
        using var sup = Sup(s);
        var zip = await NoorVox.Core.Diagnostics.SupportBundle.CreateAsync(s, sup, target, "NoorVoxStudio.exe", ct);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        return new { cancelled = false, path = zip };
    }

    static object OpenUrl(UrlArgs a)
    {
        if (!Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !UrlHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Only https links to model sources can be opened.");
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        return new { opened = uri.ToString() };
    }

    // ------------------------------------------------------------ models & voices

    static object ModelsOverview()
    {
        var s = S();
        var catalog = ModelCatalog.LoadEmbedded();
        var installed = new ModelRegistry(s).List();
        return new
        {
            modelsDir = s.ModelsDir,
            cacheDir = s.CacheDir,
            catalogVersion = catalog.CatalogVersion,
            installed = installed.Select(m => new
            {
                id = m.Id, version = m.Version, reference = m.Reference, displayName = m.Manifest.DisplayName,
                license = m.Manifest.License, licenseUrl = m.Manifest.LicenseUrl, source = m.Manifest.Source, origin = m.Manifest.Origin,
                installedAt = m.Manifest.InstalledAt, path = m.Directory, modelSha256 = m.ModelSha256,
                sizeBytes = m.Manifest.Files.Sum(f => f.Size) + m.VoicePacks.Sum(p => p.Files.Sum(f => f.Size)),
                voices = m.Voices.Count,
                capabilities = m.Capabilities, engine = m.Manifest.Engine, backend = m.Backend,
                customVoices = m.Voices.Where(v => v.PackId == VoiceBlender.CustomPackId)
                    .Select(v => VoiceBlender.Recipe(m, v.VoiceId)).Where(r => r is not null)
                    .Select(r => new { r!.VoiceId, r.Name, r.Parts, r.CreatedAt }),
                voicePacks = catalog.VoicePacksFor(m.Id, m.Version).Select(p => new
                {
                    id = p.Id, version = p.Version, displayName = p.DisplayName, description = p.Description, voices = p.Files.Count,
                    downloadBytes = p.Downloads.Sum(d => d.Size), license = p.License,
                    installed = m.VoicePacks.Any(i => i.Id == p.Id && i.Version == p.Version),
                }),
            }),
            catalog = catalog.Models.Select(c => new
            {
                id = c.Id, version = c.Version, displayName = c.DisplayName, description = c.Description, recommended = c.Recommended,
                license = c.License, licenseUrl = c.LicenseUrl,
                capabilities = c.Capabilities ?? ModelCapabilities.Kokoro, engine = c.Engine, minVramGb = c.MinVramGb, family = c.Family ?? "kokoro",
                engineInstalled = c.Engine is null || new EngineRegistry(s).Find(c.Engine) is not null,
                engineBytes = c.Engine is null ? 0 : catalog.FindEngine(c.Engine)?.ApproxInstallBytes ?? 0,
                downloadBytes = c.Downloads.Sum(d => d.Size)
                    + c.DefaultVoicePacks.Select(catalog.FindVoicePack).Where(v => v is not null).Sum(v => v!.Downloads.Sum(d => d.Size)),
                installed = installed.Any(m => m.Id == c.Id && m.Version == c.Version),
                updateAvailable = installed.Any(m => m.Id == c.Id)
                    && !installed.Any(m => m.Id == c.Id && ModelRef.ParseVersion(m.Version) >= ModelRef.ParseVersion(c.Version)),
            }),
        };
    }

    IProgress<InstallProgress> InstallProgressEvents(string target) =>
        new SyncProgress<InstallProgress>(x => bridge.Emit("install.progress", new { target, x.Stage, x.Item, x.File, x.Done, x.Total }));

    sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

    async Task<object?> ModelsInstall(IdArgs a, CancellationToken ct)
    {
        var s = S();
        var catalog = ModelCatalog.LoadEmbedded();
        var model = catalog.FindModel(a.Id) ?? throw new NoorVoxException(ErrorCodes.UnknownModel, ExitCode.ModelMissing, $"'{a.Id}' is not in the catalog.");
        if (model.Engine is { } engineId && new EngineRegistry(s).Find(engineId) is null)
            await new EngineInstaller(s, catalog).InstallAsync(engineId, InstallProgressEvents(a.Id), ct); // one click: engine, then model
        var result = await new ModelInstaller(s, catalog).InstallModelAsync(model, true, InstallProgressEvents(a.Id), ct);
        bridge.Emit("install.progress", new { target = a.Id, stage = "done" });
        return result;
    }

    async Task<object?> VoicePackInstall(IdArgs a, CancellationToken ct)
    {
        var s = S();
        var catalog = ModelCatalog.LoadEmbedded();
        var pack = catalog.FindVoicePack(a.Id) ?? throw new NoorVoxException(ErrorCodes.UnknownModel, ExitCode.ModelMissing, $"Voice pack '{a.Id}' is not in the catalog.");
        var model = new ModelRegistry(s).Resolve(pack.ModelId);
        var result = await new ModelInstaller(s, catalog).InstallVoicePackAsync(pack, model, InstallProgressEvents(a.Id), ct);
        bridge.Emit("install.progress", new { target = a.Id, stage = "done" });
        return result;
    }

    Task<object?> ModelsVerify(IdArgs a, CancellationToken ct) => Task.Run<object?>(() =>
    {
        var m = new ModelRegistry(S()).Resolve(a.Id);
        var progress = InstallProgressEvents(m.Reference);
        var files = m.Manifest.Files.Concat(m.VoicePacks.SelectMany(p => p.Files)).ToList();
        int i = 0;
        var result = ModelRegistry.Verify(m.Directory, files, full: true,
            f => progress.Report(new InstallProgress("verifying", m.Reference, f, ++i, files.Count)));
        bridge.Emit("install.progress", new { target = m.Reference, stage = "done" });
        return result;
    }, ct);

    static object VoiceBlend(BlendArgs a)
    {
        var model = new ModelRegistry(S()).Resolve(null);
        return VoiceBlender.Blend(model, a.Name, a.Parts);
    }

    /// <summary>Voices and capabilities of one installed model (default: the configured default model), plus the model list.</summary>
    static object VoicesList(string? model)
    {
        var s = S();
        var registry = new ModelRegistry(s);
        var models = registry.List();
        if (models.Count == 0) return new { model = (string?)null, voices = Array.Empty<VoiceInfo>(), defaultVoice = (string?)null, models = Array.Empty<object>() };
        var m = registry.Resolve(model);
        var engines = new EngineRegistry(s);
        string? defaultVoice = s.Settings.DefaultVoice is { } dv && m.HasVoice(dv) ? dv : m.Manifest.DefaultVoice;
        if (string.IsNullOrEmpty(defaultVoice) || !m.HasVoice(defaultVoice)) defaultVoice = m.Voices.FirstOrDefault().VoiceId;
        return new
        {
            model = m.Reference,
            modelName = m.Manifest.DisplayName,
            defaultVoice,
            capabilities = m.Capabilities,
            engineReady = m.Manifest.Engine is null || engines.Find(m.Manifest.Engine) is not null,
            voices = ModelVoices.List(m),
            models = models.Select(x => new
            {
                reference = x.Reference, id = x.Id, displayName = x.Manifest.DisplayName, capabilities = x.Capabilities,
                isDefault = registry.Resolve(null).Reference == x.Reference,
                engineReady = x.Manifest.Engine is null || engines.Find(x.Manifest.Engine) is not null,
            }),
            consentStatements = VoiceProfiles.ConsentStatements,
        };
    }

    // Reference recordings are chosen in a native dialog; the page only ever sees an opaque token and the file name.
    readonly Dictionary<string, string> _referenceTokens = new();

    async Task<object?> ChooseReference()
    {
        var path = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a recording of the voice (2-60 s of clean speech, WAV)", Filter = "WAV audio (*.wav)|*.wav", CheckFileExists = true,
            };
            return dlg.ShowDialog(window) == true ? dlg.FileName : null;
        });
        if (path is null) return new { cancelled = true };
        NoorVox.Core.Audio.WavInfo info;
        try { info = NoorVox.Core.Audio.WavInfo.Read(path); }
        catch (InvalidDataException ex) { throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"That file isn't a PCM WAV NoorVox can read: {ex.Message}"); }
        var token = Guid.NewGuid().ToString("N");
        lock (_referenceTokens) _referenceTokens[token] = path;
        return new { cancelled = false, token, fileName = Path.GetFileName(path), durationMs = info.DurationMs, sampleRate = info.SampleRate };
    }

    object VoiceClone(CloneArgs a)
    {
        string? path;
        lock (_referenceTokens) _referenceTokens.TryGetValue(a.ReferenceToken, out path);
        if (path is null) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Choose the reference recording again.");
        var s = S();
        var profile = VoiceProfiles.Create(s.ModelsDir, new CreateProfileRequest(a.Name, path, a.Transcript, a.Consent,
            (a.Language ?? "auto").ToLowerInvariant(), (a.Gender ?? "unknown").ToLowerInvariant()));
        lock (_referenceTokens) _referenceTokens.Remove(a.ReferenceToken);
        return new { profile.VoiceId, profile.Name, profile.ReferenceDurationMs, profile.Consent };
    }

    static object VoiceRemoveProfile(IdArgs a)
    {
        VoiceProfiles.Remove(S().ModelsDir, a.Id);
        return new { voiceId = a.Id, removed = true };
    }

    async Task<object?> EngineInstall(IdArgs a, CancellationToken ct)
    {
        var s = S();
        var result = await new EngineInstaller(s, ModelCatalog.LoadEmbedded()).InstallAsync(a.Id, InstallProgressEvents(a.Id), ct);
        bridge.Emit("install.progress", new { target = a.Id, stage = "done" });
        return result;
    }

    async Task RecycleIfKokoroAsync()
    {
        try
        {
            using var sup = Sup(S());
            var st = await sup.GetStatusAsync(StudioWorker, CancellationToken.None);
            if (st.LoadedModel is not null && st.Backend == "kokoro-onnx") await sup.RecycleAsync(StudioWorker, CancellationToken.None);
        }
        catch (Exception) { /* the next generation loads with the new provider anyway */ }
    }

    // ------------------------------------------------------------ workers

    async Task<object?> WorkersList(CancellationToken ct)
    {
        var s = S();
        using var sup = Sup(s);
        return new { workers = await sup.ListAsync(ct), sampledAt = DateTimeOffset.UtcNow, processorCount = Environment.ProcessorCount };
    }

    async Task<object?> WorkersStart(CancellationToken ct)
    {
        var s = S();
        using var sup = Sup(s);
        var st = await sup.GetStatusAsync(StudioWorker, ct);
        if (st.State is "stopped" or "error")
            st = await sup.StartAsync(StudioWorker, s.Settings.Workers.StopOnExit, s.Settings.Workers.IdleTimeoutSeconds, ct);
        var models = new ModelRegistry(s).List();
        if (models.Count > 0 && st.LoadedModel is null)
            st = await sup.LoadAsync(StudioWorker, new ModelRegistry(s).Resolve(null), ct);
        return st;
    }

    async Task<object?> WorkersUnload(WorkerArgs a, CancellationToken ct)
    {
        using var sup = Sup(S());
        return await sup.RecycleAsync(WorkerSupervisor.ValidateWorkerId(a.WorkerId), ct);
    }

    async Task<object?> WorkersStop(WorkerStopArgs a, CancellationToken ct)
    {
        using var sup = Sup(S());
        return await sup.StopAsync(WorkerSupervisor.ValidateWorkerId(a.WorkerId), a.Force, TimeSpan.FromSeconds(15), ct);
    }

    async Task<object?> WorkersStopAll(ForceArgs a, CancellationToken ct)
    {
        using var sup = Sup(S());
        return await sup.StopAllAsync(a.Force, TimeSpan.FromSeconds(15), ct);
    }

    // ------------------------------------------------------------ narration

    async Task<object?> Generate(GenerateArgs a, CancellationToken ct)
    {
        if (a.Text.Length > 200_000) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "Text is too long for Quick Narration (200,000 characters max).");
        var s = S();
        var dir = MainWindow.PreviewDir(s);
        Directory.CreateDirectory(dir);
        var model = new ModelRegistry(s).Resolve(a.Model);
        var voice = model.HasVoice(a.Voice) ? a.Voice : throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{a.Voice}' is not installed for {model.Reference}.");
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{voice}_{Guid.NewGuid().ToString("N")[..6]}.wav");
        using var sup = Sup(s);
        var result = await new NarrationService(s, sup).SynthesizeAsync(new NarrationRequest
        {
            Text = a.Text,
            VoiceId = voice,
            Speed = a.Speed,
            Model = model.Reference,
            Style = a.Style,
            Seed = a.Seed,
            Language = string.IsNullOrEmpty(a.Language) ? null : a.Language,
            OutputPath = file,
            WriteManifest = true,
            WorkerId = StudioWorker,
            DedicatedWorker = true,
            DedicatedBoundToLauncher = s.Settings.Workers.StopOnExit,
            DedicatedIdleTimeoutSeconds = s.Settings.Workers.IdleTimeoutSeconds,
        }, msg => bridge.Emit("narration.progress", new { message = msg }), ct);
        return new { result, mediaUrl = $"https://{MainWindow.MediaHost}/{Uri.EscapeDataString(Path.GetFileName(file))}" };
    }

    async Task<object?> Export(ExportArgs a)
    {
        var s = S();
        var preview = RequireInside(a.PreviewPath, MainWindow.PreviewDir(s));
        var exports = s.Resolve(StorageKind.Exports).Path;
        Directory.CreateDirectory(exports);
        var name = SafeName(a.SuggestedName) ?? Path.GetFileNameWithoutExtension(preview);
        var target = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export narration",
                InitialDirectory = exports,
                FileName = name + ".wav",
                DefaultExt = ".wav",
                Filter = "WAV audio (*.wav)|*.wav",
                OverwritePrompt = true,
            };
            return dlg.ShowDialog(window) == true ? dlg.FileName : null;
        });
        if (target is null) return new { cancelled = true };

        File.Copy(preview, target, overwrite: true); // the dialog already confirmed any replacement
        string? manifestOut = null;
        var manifestIn = Path.ChangeExtension(preview, ".noorvox.json");
        if (File.Exists(manifestIn))
        {
            var m = Json.ReadFile<NarrationResult>(manifestIn);
            manifestOut = Path.ChangeExtension(target, ".noorvox.json");
            NoorVox.Core.Util.FileUtil.WriteAllTextAtomic(manifestOut, Json.Serialize(m with { OutputPath = target, ManifestPath = manifestOut }));
        }
        return new { cancelled = false, path = target, manifestPath = manifestOut };
    }

    static object RevealFile(PathArgs a)
    {
        var s = S();
        var roots = new[] { MainWindow.PreviewDir(s), s.Resolve(StorageKind.Exports).Path, s.ModelsDir };
        var full = Path.GetFullPath(a.Path);
        // Exports chosen in the save dialog may live anywhere; only reveal files NoorVox wrote a manifest for, or inside its storage.
        bool allowed = roots.Any(r => full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || File.Exists(Path.ChangeExtension(full, ".noorvox.json"));
        if (!allowed || !File.Exists(full))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "That file can't be revealed.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
        return new { path = full };
    }

    static string RequireInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Unknown preview file.");
        return full;
    }

    static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = new string(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}

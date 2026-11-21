using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NoorVox.Core;
using NoorVox.Core.Export;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Studio.Bridge;

/// <summary>
/// Project methods for the UI. A project path is only accepted if it is in the recent-projects list, and that list is only
/// extended by native dialogs or by creating a project, so JavaScript can never point NoorVox at arbitrary folders.
/// </summary>
public sealed class ProjectsApi(MainWindow window, JobObject job, BridgeHost bridge, string workerExe)
{
    sealed class Running(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public volatile bool StopAfterCurrent;
    }

    readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);

    public sealed record PathArgs(string Path);
    public sealed record CreateArgs(string Title, string Kind, string? Voice, string? Model);
    public sealed record SegmentArgs(string Path, string SegmentId);
    /// <param name="Style">null = unchanged; "" = inherit from the speaker/project again.</param>
    public sealed record UpdateSegmentArgs(string Path, string SegmentId, string? Text, string? SpeakerId, string? Notes, string? VoiceId, bool ClearVoice, string? Style = null);
    public sealed record AddSegmentArgs(string Path, string SceneId, string? AfterSegmentId);
    /// <param name="Style">null = unchanged; "" = none.</param> <param name="Model">Project-wide model change (only without SpeakerId).</param>
    public sealed record CastArgs(string Path, string? SpeakerId, string VoiceId, string? Style = null, string? Model = null);
    public sealed record GenerateArgs(string Path, string? SectionId, string? SceneId, List<string>? SegmentIds, string Policy, bool Approve);
    public sealed record StopArgs(string Path, bool Now);
    public sealed record ResumeArgs(string Path, string JobId);
    public sealed record ApproveArgs(string Path, string SegmentId, string? TakeId);
    public sealed record ExportArgs(string Path, string? SectionId, bool AllowMissing);
    public sealed record RenameArgs(string Path, string Title);
    public sealed record SplitArgs(string Path, string SegmentId, int At);
    public sealed record PronunciationArgs(string Path, List<PronunciationRule> Rules);
    public sealed record AssembleArgs(string Path, string? SectionId, int? Rate, bool Normalize, bool Subtitles, bool AllowMissing);

    public async Task<object?> InvokeAsync(string method, JsonElement? p, CancellationToken ct) => method switch
    {
        "projects.kinds" => ProjectKinds.All,
        "projects.recent" => Recent(),
        "projects.create" => await Create(Arg<CreateArgs>(p)),
        "projects.openDialog" => await OpenDialog(),
        "projects.forget" => Forget(Arg<PathArgs>(p)),
        "projects.rename" => Rename(Arg<RenameArgs>(p)),
        "projects.split" => SplitSegment(Arg<SplitArgs>(p)),
        "projects.merge" => MergeSegment(Arg<SegmentArgs>(p)),
        "projects.pronunciation" => OpenAllowed(Arg<PathArgs>(p).Path).Pronunciation(),
        "projects.setPronunciation" => SetPronunciation(Arg<PronunciationArgs>(p)),
        "projects.duplicate" => await DuplicateProject(Arg<PathArgs>(p)),
        "projects.overview" => Overview(Arg<PathArgs>(p), remap: true),
        "projects.import" => await Import(Arg<PathArgs>(p)),
        "projects.updateSegment" => UpdateSegment(Arg<UpdateSegmentArgs>(p)),
        "projects.addSegment" => AddSegment(Arg<AddSegmentArgs>(p)),
        "projects.removeSegment" => RemoveSegment(Arg<SegmentArgs>(p)),
        "projects.cast" => Cast(Arg<CastArgs>(p)),
        "projects.generate" => Generate(Arg<GenerateArgs>(p)),
        "projects.resume" => Resume(Arg<ResumeArgs>(p)),
        "projects.stop" => Stop(Arg<StopArgs>(p)),
        "projects.takes" => Takes(Arg<SegmentArgs>(p)),
        "projects.approve" => Approve(Arg<ApproveArgs>(p)),
        "projects.export" => await Export(Arg<ExportArgs>(p)),
        "projects.assemble" => await Assemble(Arg<AssembleArgs>(p)),
        "projects.revealOutput" => RevealOutput(Arg<RevealOutputArgs>(p)),
        "projects.reveal" => Reveal(Arg<PathArgs>(p)),
        _ => throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown bridge method '{method}'."),
    };

    static T Arg<T>(JsonElement? p) where T : class =>
        (p is { ValueKind: JsonValueKind.Object } e ? e.Deserialize<T>(Json.Compact) : null)
        ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Missing or invalid parameters ({typeof(T).Name}).");

    // ------------------------------------------------------------ recents (the allow-list)

    static void Remember(string root)
    {
        var s = StorageContext.Load();
        var list = s.Settings.Studio.RecentProjects;
        list.RemoveAll(x => string.Equals(Path.GetFullPath(x), root, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, root);
        if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
        s.Save();
    }

    static ProjectStore OpenAllowed(string path)
    {
        var full = Path.GetFullPath(path);
        var recents = StorageContext.Load().Settings.Studio.RecentProjects;
        if (!recents.Any(r => string.Equals(Path.GetFullPath(r), full, StringComparison.OrdinalIgnoreCase)))
            throw new NoorVoxException(ErrorCodes.PermissionDenied, ExitCode.InvalidArguments, "Open this project with Open project… first.");
        return ProjectStore.Open(full);
    }

    static object Recent() => StorageContext.Load().Settings.Studio.RecentProjects.Select(r =>
    {
        try
        {
            var proj = Json.ReadFile<ProjectFile>(Path.Combine(r, ProjectStore.ProjectFileName));
            return (object)new { path = r, exists = true, title = proj.Title, kind = proj.Kind, sections = proj.Sections.Count, modifiedAt = proj.ModifiedAt };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new { path = r, exists = false, title = Path.GetFileName(r), kind = "", sections = 0, modifiedAt = (DateTimeOffset?)null };
        }
    }).ToList();

    object Forget(PathArgs a)
    {
        var s = StorageContext.Load();
        s.Settings.Studio.RecentProjects.RemoveAll(x => string.Equals(x, a.Path, StringComparison.OrdinalIgnoreCase));
        s.Save();
        return Recent();
    }

    async Task<object?> Create(CreateArgs a)
    {
        ProjectKinds.Get(a.Kind);
        if (string.IsNullOrWhiteSpace(a.Title)) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Give the project a name.");
        var projects = StorageContext.Load().Resolve(StorageKind.Projects).Path;
        Directory.CreateDirectory(projects);
        var parent = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to create the project folder", InitialDirectory = projects };
            return dlg.ShowDialog(window) == true ? dlg.FolderName : null;
        });
        if (parent is null) return new { cancelled = true };
        var safe = new string(a.Title.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var dir = Path.Combine(parent, safe);
        for (int i = 2; Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); i++) dir = Path.Combine(parent, $"{safe} ({i})");
        string? model = null;
        if (!string.IsNullOrWhiteSpace(a.Model)) model = new ModelRegistry(StorageContext.Load()).Resolve(a.Model).Id; // must be installed
        var store = ProjectStore.Create(dir, a.Title, a.Kind, string.IsNullOrWhiteSpace(a.Voice) ? null : a.Voice, model);
        Remember(store.Root);
        return new { cancelled = false, path = store.Root };
    }

    static object SplitSegment(SplitArgs a)
    {
        var (first, second) = OpenAllowed(a.Path).SplitSegment(a.SegmentId, a.At);
        return new { first = first.SegmentId, second = second.SegmentId };
    }

    static object MergeSegment(SegmentArgs a) => new { merged = OpenAllowed(a.Path).MergeWithNext(a.SegmentId).SegmentId };

    static object SetPronunciation(PronunciationArgs a)
    {
        var p = OpenAllowed(a.Path);
        int changed = p.SavePronunciation(a.Rules);
        return new { rules = p.Pronunciation().Rules, segmentsChanged = changed };
    }

    static object Rename(RenameArgs a)
    {
        OpenAllowed(a.Path).Rename(a.Title);
        return Recent();
    }

    async Task<object?> DuplicateProject(PathArgs a)
    {
        var p = OpenAllowed(a.Path);
        var parentDefault = Path.GetDirectoryName(p.Root)!;
        var parent = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Where should the copy of “{p.Project.Title}” go?", InitialDirectory = parentDefault };
            return dlg.ShowDialog(window) == true ? dlg.FolderName : null;
        });
        if (parent is null) return new { cancelled = true };
        var baseName = Path.GetFileName(p.Root) + " (copy)";
        var dest = Path.Combine(parent, baseName);
        for (int i = 2; Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any(); i++) dest = Path.Combine(parent, $"{baseName} {i}");
        var copy = await Task.Run(() => p.Duplicate(dest));
        Remember(copy.Root);
        return new { cancelled = false, path = copy.Root, title = copy.Project.Title };
    }

    async Task<object?> OpenDialog()
    {
        var initial = StorageContext.Load().Resolve(StorageKind.Projects).Path;
        var folder = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Open a NoorVox project folder", InitialDirectory = Directory.Exists(initial) ? initial : "" };
            return dlg.ShowDialog(window) == true ? dlg.FolderName : null;
        });
        if (folder is null) return new { cancelled = true };
        var store = ProjectStore.Open(folder); // throws a clear error if it isn't a project
        Remember(store.Root);
        return new { cancelled = false, path = store.Root };
    }

    // ------------------------------------------------------------ overview & editing

    static InstalledModel? ModelOrNull(ProjectStore p)
    {
        try { return ProjectGeneration.ResolveModel(StorageContext.Load(), p); }
        catch (NoorVoxException e) when (e.Code == ErrorCodes.ModelNotInstalled) { return null; }
    }

    object Overview(PathArgs a, bool remap)
    {
        var p = OpenAllowed(a.Path);
        if (remap) Application.Current.Dispatcher.Invoke(() => window.MapProjectTakes(p.TakesDir));
        var o = ProjectOverview.Build(p, ModelOrNull(p));
        return new { overview = o, running = _running.ContainsKey(p.Root) };
    }

    async Task<object?> Import(PathArgs a)
    {
        var p = OpenAllowed(a.Path);
        var file = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a script",
                Filter = "Scripts (*.md;*.txt;*.csv;*.json)|*.md;*.txt;*.csv;*.json|Markdown or text (*.md;*.txt)|*.md;*.txt|Dialogue table (*.csv;*.json)|*.csv;*.json",
            };
            return dlg.ShowDialog(window) == true ? dlg.FileName : null;
        });
        if (file is null) return new { cancelled = true };
        var (sections, reference) = ScriptImporter.ImportInto(p, file);
        return new { cancelled = false, reference, sections = sections.Select(s => new { s.SectionId, s.Title }), firstSceneId = sections[0].Scenes.FirstOrDefault()?.SceneId };
    }

    object SegmentView(ProjectStore p, string segmentId)
    {
        var o = ProjectOverview.Build(p, ModelOrNull(p));
        return o.Sections.SelectMany(s => s.Scenes).SelectMany(s => s.Segments).First(s => s.SegmentId == segmentId);
    }

    object UpdateSegment(UpdateSegmentArgs a)
    {
        var p = OpenAllowed(a.Path);
        if (a.Text is { Length: > 20_000 }) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "A segment can hold up to 20,000 characters. Split it.");
        if (a.Style is { Length: > NoorVox.Core.Narration.NarrationService.MaxStyleChars })
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Keep style directions under {NoorVox.Core.Narration.NarrationService.MaxStyleChars} characters.");
        if (a.SpeakerId is { Length: > 0 } sp && p.Project.Speakers.All(s => s.SpeakerId != sp))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown speaker '{sp}'.");
        p.UpdateSegment(a.SegmentId, s =>
        {
            if (a.Text is not null) s.Text = a.Text;
            if (a.SpeakerId is not null) s.SpeakerId = a.SpeakerId.Length == 0 ? null : a.SpeakerId;
            if (a.Notes is not null) s.Notes = a.Notes.Length == 0 ? null : a.Notes;
            if (a.ClearVoice) s.VoiceId = null;
            else if (a.VoiceId is { Length: > 0 }) s.VoiceId = a.VoiceId;
            if (a.Style is not null) s.Style = a.Style.Trim().Length == 0 ? null : a.Style.Trim();
        });
        return SegmentView(p, a.SegmentId);
    }

    object AddSegment(AddSegmentArgs a)
    {
        var p = OpenAllowed(a.Path);
        var seg = p.AddSegment(a.SceneId, a.AfterSegmentId);
        return new { segmentId = seg.SegmentId };
    }

    object RemoveSegment(SegmentArgs a)
    {
        var p = OpenAllowed(a.Path);
        p.RemoveSegment(a.SegmentId);
        return new { removed = a.SegmentId, note = "Its takes were kept on disk." };
    }

    object Cast(CastArgs a)
    {
        var p = OpenAllowed(a.Path);
        if (a.Model is not null && a.SpeakerId is not null)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "The model is chosen for the whole project.");
        var model = a.Model is not null ? new ModelRegistry(StorageContext.Load()).Resolve(a.Model)
            : ModelOrNull(p) ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "Install a voice model first.");
        if (!model.HasVoice(a.VoiceId))
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{a.VoiceId}' is not installed for {model.Manifest.DisplayName}.");
        var style = a.Style?.Trim();
        NoorVox.Core.Narration.NarrationService.CheckCapabilities(model, 1.0, style, null, null);
        p.MutateProject(proj =>
        {
            if (a.SpeakerId is null)
            {
                if (a.Model is not null)
                {
                    proj.Defaults.Model = model.Id;
                    if (!model.Capabilities.Speed) proj.Defaults.Speed = 1.0; // e.g. Qwen has no speed control
                }
                proj.Defaults.VoiceId = a.VoiceId;
                if (style is not null) proj.Defaults.Style = style.Length == 0 ? null : style;
                return;
            }
            var sp = proj.Speakers.FirstOrDefault(s => s.SpeakerId == a.SpeakerId)
                ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Unknown speaker '{a.SpeakerId}'.");
            sp.VoiceId = a.VoiceId;
            if (style is not null) sp.Style = style.Length == 0 ? null : style;
        });
        return new { defaults = p.Project.Defaults, speakers = p.Project.Speakers };
    }

    // ------------------------------------------------------------ generation (background, one job per project)

    object Generate(GenerateArgs a)
    {
        var p = OpenAllowed(a.Path);
        var model = ModelOrNull(p) ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "Install a voice model first.");
        var created = new JobRunner(p, model).Create(new JobScope { SectionId = a.SectionId, SceneId = a.SceneId, SegmentIds = a.SegmentIds }, a.Policy, a.Approve);
        Start(p, model, created);
        return new { jobId = created.JobId, total = created.Total };
    }

    object Resume(ResumeArgs a)
    {
        var p = OpenAllowed(a.Path);
        var model = ModelOrNull(p) ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "Install a voice model first.");
        var j = new JobRunner(p, model).Load(a.JobId);
        Start(p, model, j);
        return new { jobId = j.JobId, total = j.Total };
    }

    void Start(ProjectStore p, InstalledModel model, GenerationJob j)
    {
        var running = new Running(new CancellationTokenSource());
        if (!_running.TryAdd(p.Root, running))
            throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, "This project is already generating.");
        var root = p.Root;
        _ = Task.Run(async () =>
        {
            object? error = null;
            GenerationJob result = j;
            try
            {
                var s = StorageContext.Load();
                using var sup = new WorkerSupervisor(s, workerExe, "studio", job);
                var progress = new SyncProgress<JobProgress>(pr => bridge.Emit("job.progress", new { path = root, pr.JobId, pr.State, pr.Done, pr.Total, pr.Failed, pr.CurrentSegmentId, pr.LastItem }));
                result = await ProjectGeneration.RunAsync(s, sup, p, model, j,
                    new WorkerPolicy { WorkerId = StudioApi.StudioWorker, Dedicated = true, DedicatedBoundToLauncher = s.Settings.Workers.StopOnExit, DedicatedIdleTimeoutSeconds = s.Settings.Workers.IdleTimeoutSeconds },
                    progress, msg => bridge.Emit("job.status", new { path = root, message = msg }), () => running.StopAfterCurrent, running.Cts.Token);
            }
            catch (OperationCanceledException) { error = new { code = ErrorCodes.Cancelled, message = "Stopped. Finished takes were kept; you can resume." }; }
            catch (NoorVoxException ex) { error = new { code = ex.Code, message = ex.Message }; }
            catch (Exception ex) { StudioLog.Write($"job failed: {ex}"); error = new { code = ErrorCodes.InternalError, message = ex.Message }; }
            finally { _running.TryRemove(root, out _); }
            try { result = new JobRunner(p, model).Load(j.JobId); } catch (NoorVoxException) { }
            bridge.Emit("job.finished", new { path = root, job = result, total = result.Total, done = result.Done, failed = result.FailedCount, error });
        });
    }

    object Stop(StopArgs a)
    {
        var root = Path.GetFullPath(a.Path);
        if (!_running.TryGetValue(root, out var r)) return new { stopping = false };
        r.StopAfterCurrent = true;
        if (a.Now) r.Cts.Cancel();
        return new { stopping = true, now = a.Now };
    }

    sealed class SyncProgress<T>(Action<T> act) : IProgress<T> { public void Report(T v) => act(v); }

    // ------------------------------------------------------------ takes & export

    object Takes(SegmentArgs a)
    {
        var p = OpenAllowed(a.Path);
        var loc = p.Find(a.SegmentId);
        var model = ModelOrNull(p);
        var eff = model is null ? null : p.Effective(loc.Segment, model);
        return p.Takes(a.SegmentId).Reverse().Select(t => new
        {
            t.TakeId, t.DurationMs, t.VoiceId, t.Speed, t.CreatedAt, t.SpokenText,
            approved = t.TakeId == loc.Segment.ApprovedTakeId,
            matchesCurrent = eff is not null && t.CacheKey == eff.CacheKey,
            mediaUrl = $"https://{MainWindow.TakesHost}/{Uri.EscapeDataString(t.SegmentId)}/{Uri.EscapeDataString(t.TakeId)}.wav",
        }).ToList();
    }

    object Approve(ApproveArgs a)
    {
        var p = OpenAllowed(a.Path);
        p.Approve(a.SegmentId, a.TakeId);
        return SegmentView(p, a.SegmentId);
    }

    async Task<object?> Export(ExportArgs a)
    {
        var p = OpenAllowed(a.Path);
        var model = ModelOrNull(p) ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "Install a voice model first.");
        var parent = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Export approved clips into…", InitialDirectory = Path.Combine(p.Root, "exports") };
            return dlg.ShowDialog(window) == true ? dlg.FolderName : null;
        });
        if (parent is null) return new { cancelled = true };
        var name = $"{a.SectionId ?? "all"}_clips_{DateTime.Now:yyyyMMdd_HHmmss}";
        var r = ClipExporter.Export(p, model, a.SectionId, Path.Combine(parent, name), a.AllowMissing, overwrite: false);
        return new { cancelled = false, result = r };
    }

    async Task<object?> Assemble(AssembleArgs a)
    {
        var p = OpenAllowed(a.Path);
        var model = ModelOrNull(p) ?? throw new NoorVoxException(ErrorCodes.ModelNotInstalled, ExitCode.ModelMissing, "Install a voice model first.");
        if (a.Rate is not null and not (24_000 or 44_100 or 48_000))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "Sample rate must be 24000, 44100 or 48000.");
        var sections = a.SectionId is { } one ? [one] : p.Project.Sections.Select(s => s.SectionId).ToList();
        // Check approvals before asking for a folder, so the user isn't sent through a dialog for nothing.
        if (!a.AllowMissing)
        {
            var missing = p.AllSegments().Where(l => sections.Contains(l.Section.SectionId) && !string.IsNullOrWhiteSpace(l.Segment.Text)
                && (l.Segment.ApprovedTakeId is null || p.Takes(l.Segment.SegmentId).All(t => t.TakeId != l.Segment.ApprovedTakeId))).Count();
            if (missing > 0)
                throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"{missing} segment(s) have no approved take.", new { total = missing });
        }
        var parent = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Assemble into…", InitialDirectory = Path.Combine(p.Root, "exports") };
            return dlg.ShowDialog(window) == true ? dlg.FolderName : null;
        });
        if (parent is null) return new { cancelled = true };
        var outDir = Path.Combine(parent, $"assembled_{DateTime.Now:yyyyMMdd_HHmmss}");
        var options = new AssemblyOptions { SampleRate = a.Rate, NormalizePeak = a.Normalize, Subtitles = a.Subtitles, AllowMissing = a.AllowMissing };
        var results = await Task.Run(() => sections.Select(id =>
        {
            bridge.Emit("job.status", new { path = p.Root, message = $"Assembling {id}…" });
            return Assembler.AssembleSection(p, model, id, outDir, options);
        }).ToList());
        return new
        {
            cancelled = false,
            outputDir = outDir,
            sections = results.Select(r => new { r.AudioPath, r.SrtPath, r.VttPath, r.ManifestPath, r.Manifest.Audio.DurationMs, r.Manifest.Audio.SampleRate,
                segments = r.Manifest.Segments.Count, missing = r.Manifest.Missing.Count, stale = r.Manifest.Stale.Count }),
        };
    }

    public sealed record RevealOutputArgs(string Folder);

    /// <summary>Opens an export folder, but only one that NoorVox wrote (it holds an assembly manifest or clips.json).</summary>
    static object RevealOutput(RevealOutputArgs a)
    {
        var dir = Path.GetFullPath(a.Folder);
        bool ours = Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "clips.json")) || Directory.EnumerateFiles(dir, "*.manifest.json").Any());
        if (!ours) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "That folder can't be opened from here.");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        return new { path = dir };
    }

    static object Reveal(PathArgs a)
    {
        var p = OpenAllowed(a.Path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.Root}\"") { UseShellExecute = true });
        return new { path = p.Root };
    }

    /// <summary>Called on window close: stop running jobs (each finishes its current segment's commit, or is interrupted).</summary>
    public void CancelAll()
    {
        foreach (var r in _running.Values) r.Cts.Cancel();
    }
}

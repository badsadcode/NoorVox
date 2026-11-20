using NoorVox.Core;
using NoorVox.Core.Export;
using NoorVox.Core.Models;
using NoorVox.Core.Projects;
using NoorVox.Core.Storage;
using NoorVox.Core.Workers;

namespace NoorVox.Cli;

/// <summary>Project, generation, job, take and export commands (Phase 3).</summary>
public static class ProjectCommands
{
    static StorageContext Storage() => StorageContext.Load();
    static ProjectStore Open(CommandContext ctx) => ProjectStore.Open(ctx.Args.Require("project"));
    static InstalledModel Model(StorageContext s, ProjectStore p) => ProjectGeneration.ResolveModel(s, p);
    static InstalledModel? ModelOrNull(StorageContext s, ProjectStore p)
    {
        try { return Model(s, p); } catch (NoorVoxException e) when (e.Code == ErrorCodes.ModelNotInstalled) { return null; }
    }

    static WorkerSupervisor Supervisor(StorageContext s) =>
        new(s, Environment.GetEnvironmentVariable("NOORVOX_WORKER_EXE") is { Length: > 0 } p ? Path.GetFullPath(p)
            : Path.Combine(AppContext.BaseDirectory, "NoorVox.Worker.exe"), "cli");

    static object Summary(ProjectOverview o) => new
    {
        root = o.Root,
        projectId = o.Project.ProjectId,
        title = o.Project.Title,
        kind = o.Kind.Id,
        labels = new { section = o.Kind.SectionLabel, scene = o.Kind.SceneLabel, segment = o.Kind.SegmentLabel },
        model = o.Model,
        defaults = o.Project.Defaults,
        speakers = o.Project.Speakers,
        segments = o.SegmentCount,
        counts = o.Counts,
        approvedDurationMs = o.ApprovedDurationMs,
        sections = o.Sections.Select(s => new { s.SectionId, s.Title, scenes = s.Scenes.Count, segments = s.Scenes.Sum(x => x.Segments.Count), counts = s.Counts }),
        recentJobs = o.RecentJobs.Select(JobSummary),
        warnings = o.Warnings,
    };

    static object JobSummary(GenerationJob j) => new
    {
        j.JobId, j.State, j.Policy, j.AutoApprove, j.Scope, j.Model, j.CreatedAt, j.StartedAt, j.FinishedAt,
        total = j.Total, done = j.Done, failed = j.FailedCount, remaining = j.Remaining,
        generated = j.Items.Count(i => i.State == ItemStates.Generated),
        reused = j.Items.Count(i => i.State == ItemStates.Reused),
        skipped = j.Items.Count(i => i.State == ItemStates.Skipped),
    };

    // ---------------------------------------------------------------- projects

    public static Task<CommandResult> Create(CommandContext ctx)
    {
        var a = ctx.Args;
        var p = ProjectStore.Create(a.Require("path"), a.Require("title"), a.Get("kind") ?? "narration", a.Get("voice"), a.Get("model"));
        return Task.FromResult(new CommandResult(new { root = p.Root, project = p.Project, kind = p.Kind },
            $"Created {p.Kind.DisplayName} project '{p.Project.Title}' in {p.Root}."));
    }

    public static Task<CommandResult> Import(CommandContext ctx)
    {
        var p = Open(ctx);
        var (sections, reference) = ScriptImporter.ImportInto(p, ctx.Args.Require("file"), ctx.Args.Get("format"));
        var data = new
        {
            reference,
            sections = sections.Select(s => new { s.SectionId, s.Title, scenes = s.Scenes.Count, segments = s.Scenes.Sum(x => x.Segments.Count),
                firstSegmentId = s.Scenes.FirstOrDefault()?.Segments.FirstOrDefault()?.SegmentId }),
            speakers = p.Project.Speakers,
        };
        return Task.FromResult(new CommandResult(data,
            $"Imported {sections.Count} {p.Kind.SectionLabel.ToLowerInvariant()}(s) with {sections.Sum(s => s.Scenes.Sum(x => x.Segments.Count))} {p.Kind.SegmentLabel.ToLowerInvariant()}(s)."));
    }

    public static Task<CommandResult> Show(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var o = ProjectOverview.Build(p, ModelOrNull(s, p), includeText: !ctx.Args.Has("no-text"));
        object data = ctx.Args.Has("segments") ? new { summary = Summary(o), sections = o.Sections } : Summary(o);
        return Task.FromResult(new CommandResult(data,
            $"{o.Project.Title}: {o.SegmentCount} segment(s) — " + string.Join(", ", o.Counts.Select(kv => $"{kv.Value} {kv.Key}"))));
    }

    public static Task<CommandResult> Validate(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var problems = ProjectOverview.Validate(p, ModelOrNull(s, p));
        if (problems.Count > 0)
            throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, $"Project has {problems.Count} problem(s).", new { problems });
        return Task.FromResult(new CommandResult(new { root = p.Root, problems }, "Project is valid."));
    }

    public static Task<CommandResult> Cast(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var voice = ctx.Args.Require("voice");
        var model = Model(s, p);
        if (!model.Voices.Any(v => v.VoiceId == voice))
            throw new NoorVoxException(ErrorCodes.VoiceNotFound, ExitCode.ModelMissing, $"Voice '{voice}' is not installed for {model.Reference}.");
        double? speed = ctx.Args.Has("speed") ? ctx.Args.GetDouble("speed", 1) : null;
        if (speed is < 0.5 or > 2.0) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "--speed must be 0.5–2.0.");
        var speakerId = ctx.Args.Get("speaker");
        p.MutateProject(proj =>
        {
            if (speakerId is null)
            {
                proj.Defaults.VoiceId = voice;
                if (speed is { } sp) proj.Defaults.Speed = sp;
                return;
            }
            var sp2 = proj.Speakers.FirstOrDefault(x => x.SpeakerId == speakerId)
                ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"No speaker '{speakerId}'. Known: {string.Join(", ", proj.Speakers.Select(x => x.SpeakerId))}");
            sp2.VoiceId = voice;
            if (speed is not null) sp2.Speed = speed;
        });
        return Task.FromResult(new CommandResult(new { defaults = p.Project.Defaults, speakers = p.Project.Speakers },
            speakerId is null ? $"Default voice set to {voice}." : $"Speaker '{speakerId}' now speaks with {voice}."));
    }

    // ---------------------------------------------------------------- generate / jobs

    public static async Task<CommandResult> Generate(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var model = Model(s, p);
        var scope = new JobScope
        {
            SectionId = ctx.Args.Get("section"),
            SceneId = ctx.Args.Get("scene"),
            SegmentIds = ctx.Args.Get("segment")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
        var job = new JobRunner(p, model).Create(scope, ctx.Args.Get("policy") ?? "missing", ctx.Args.Has("approve"));
        ctx.Info($"Job {job.JobId}: {job.Total} segment(s), policy {job.Policy}{(job.AutoApprove ? ", auto-approve" : "")}. Resume after interruption: noorvox jobs resume --project \"{p.Root}\" --job {job.JobId}");
        return await RunJob(ctx, s, p, model, job);
    }

    public static async Task<CommandResult> Resume(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var model = Model(s, p);
        var job = new JobRunner(p, model).Load(ctx.Args.Require("job"));
        if (job.State == JobStates.Running)
            throw new NoorVoxException(ErrorCodes.WorkerBusy, ExitCode.WorkerBusy, $"Job {job.JobId} is running right now.");
        ctx.Info($"Resuming {job.JobId}: {job.Remaining + job.FailedCount} of {job.Total} segment(s) left.");
        return await RunJob(ctx, s, p, model, job);
    }

    static async Task<CommandResult> RunJob(CommandContext ctx, StorageContext s, ProjectStore p, InstalledModel model, GenerationJob job)
    {
        using var sup = Supervisor(s);
        var progress = new SyncProgress<JobProgress>(pr =>
        {
            if (pr.LastItem is { } it)
                ctx.Info($"[{pr.Done + pr.Failed}/{pr.Total}] {it.SegmentId} {it.State}{(it.DurationMs is { } d ? $" ({d / 1000.0:0.0} s)" : "")}{(it.Error is { } e ? $": {e}" : "")}");
        });
        var doneBefore = job.Items.Where(i => ItemStates.IsDone(i.State)).Select(i => i.SegmentId).ToHashSet();
        GenerationJob result;
        try
        {
            result = await ProjectGeneration.RunAsync(s, sup, p, model, job,
                new WorkerPolicy { WorkerId = ctx.Args.Get("worker"), KeepWorker = ctx.Args.Has("keep-worker") },
                progress, msg => ctx.Progress(msg, force: true), null, ctx.Ct);
        }
        catch (OperationCanceledException)
        {
            throw new NoorVoxException(ErrorCodes.Cancelled, ExitCode.Cancelled,
                $"Interrupted. Finished takes were kept. Resume with: noorvox jobs resume --project \"{p.Root}\" --job {job.JobId}",
                new { jobId = job.JobId, resume = $"noorvox jobs resume --project \"{p.Root}\" --job {job.JobId}" });
        }
        var thisRun = result.Items.Where(i => !doneBefore.Contains(i.SegmentId)).ToList();
        var data = new
        {
            job = JobSummary(result),
            // Counts in `job` are cumulative over every run of this job; `thisRun` is only what this command did.
            thisRun = new
            {
                generated = thisRun.Count(i => i.State == ItemStates.Generated),
                reused = thisRun.Count(i => i.State == ItemStates.Reused),
                skipped = thisRun.Count(i => i.State == ItemStates.Skipped),
                failed = thisRun.Count(i => i.State == ItemStates.Failed),
            },
            items = result.Items,
            project = p.Root,
        };
        if (result.FailedCount > 0)
            throw new NoorVoxException(ErrorCodes.SynthesisFailed, ExitCode.SynthesisFailed,
                $"{result.FailedCount} of {result.Total} segment(s) failed; {result.Done} succeeded and were kept. Fix and run: noorvox jobs resume --project \"{p.Root}\" --job {result.JobId}", data);
        return new CommandResult(data, $"Job {result.JobId} {result.State} ({result.Done}/{result.Total}). This run: {data.thisRun.generated} generated, " +
            $"{data.thisRun.reused} reused, {data.thisRun.skipped} skipped.");
    }

    sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

    public static Task<CommandResult> JobsList(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var jobs = new JobRunner(p, Model(s, p)).List();
        return Task.FromResult(new CommandResult(new { jobs = jobs.Select(JobSummary) }, $"{jobs.Count} job(s)."));
    }

    public static Task<CommandResult> JobsStatus(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var job = new JobRunner(p, Model(s, p)).Load(ctx.Args.Require("job"));
        return Task.FromResult(new CommandResult(new { job = JobSummary(job), items = job.Items }, $"Job {job.JobId} is {job.State} ({job.Done}/{job.Total})."));
    }

    // ---------------------------------------------------------------- takes

    public static Task<CommandResult> TakesList(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var segId = ctx.Args.Require("segment");
        var loc = p.Find(segId);
        var model = ModelOrNull(s, p);
        var takes = p.Takes(segId);
        var eff = model is null ? null : p.Effective(loc.Segment, model);
        return Task.FromResult(new CommandResult(new
        {
            segmentId = segId,
            text = loc.Segment.Text,
            status = eff is null ? null : p.Status(loc.Segment, eff, takes),
            approvedTakeId = loc.Segment.ApprovedTakeId,
            takes = takes.Select(t => new
            {
                t.TakeId, path = p.SafePath(t.File), t.DurationMs, t.VoiceId, t.Speed, t.CreatedAt, t.JobId,
                approved = t.TakeId == loc.Segment.ApprovedTakeId, matchesCurrent = eff is not null && t.CacheKey == eff.CacheKey, t.SpokenText,
            }),
        }, $"{takes.Count} take(s) for {segId}."));
    }

    public static Task<CommandResult> Approve(CommandContext ctx)
    {
        var p = Open(ctx);
        var segId = ctx.Args.Require("segment");
        var takes = p.Takes(segId);
        string takeId = ctx.Args.Has("latest")
            ? takes.LastOrDefault()?.TakeId ?? throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"{segId} has no takes yet.")
            : ctx.Args.Require("take");
        p.Approve(segId, takeId);
        return Task.FromResult(new CommandResult(new { segmentId = segId, approvedTakeId = takeId }, $"Approved {takeId} for {segId}."));
    }

    public static Task<CommandResult> Unapprove(CommandContext ctx)
    {
        var p = Open(ctx);
        var segId = ctx.Args.Require("segment");
        p.Approve(segId, null);
        return Task.FromResult(new CommandResult(new { segmentId = segId, approvedTakeId = (string?)null }, $"{segId} has no approved take now (files kept)."));
    }

    // ---------------------------------------------------------------- export

    public static Task<CommandResult> Assemble(CommandContext ctx) => Task.Run(() =>
    {
        var s = Storage();
        var p = Open(ctx);
        var model = Model(s, p);
        var a = ctx.Args;
        int? rate = a.Has("rate") ? a.GetInt("rate", 0) : null;
        if (rate is not null and not (16_000 or 22_050 or 24_000 or 32_000 or 44_100 or 48_000 or 96_000))
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "--rate must be one of 16000, 22050, 24000, 32000, 44100, 48000, 96000.");
        var peak = a.GetDouble("peak-dbfs", -1.0);
        if (peak is > 0 or < -30) throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, "--peak-dbfs must be between -30 and 0.");
        var options = new AssemblyOptions
        {
            SampleRate = rate, NormalizePeak = a.Has("normalize"), PeakDbfs = peak,
            AllowMissing = a.Has("allow-missing"), Overwrite = a.Has("overwrite"), Subtitles = !a.Has("no-subtitles"),
        };
        var sections = a.Get("section") is { } one ? [one] : p.Project.Sections.Select(x => x.SectionId).ToList();
        if (sections.Count == 0) throw new NoorVoxException(ErrorCodes.InvalidInput, ExitCode.InvalidInput, "The project has no sections yet.");
        var outDir = Path.GetFullPath(a.Get("out") ?? Path.Combine(p.Root, "exports", $"assembled_{DateTime.Now:yyyyMMdd_HHmmss}"));

        var results = new List<AssemblyResult>();
        foreach (var id in sections)
        {
            ctx.Progress($"Assembling {id}...", force: true);
            results.Add(Assembler.AssembleSection(p, model, id, outDir, options));
        }
        string? index = null;
        if (results.Count > 1)
        {
            index = Path.Combine(outDir, "assembly_index.json");
            NoorVox.Core.Util.FileUtil.WriteAllTextAtomic(index, Json.Serialize(new
            {
                schemaVersion = 1, kind = "noorvox.assembly-index", generator = $"NoorVox {NoorVoxInfo.Version}", createdAt = DateTimeOffset.Now,
                project = new { p.Project.ProjectId, p.Project.Title, p.Project.Kind, root = p.Root },
                sections = results.Select(r => new { r.Manifest.Audio.File, manifest = r.ManifestPath, r.Manifest.Audio.DurationMs }),
                totalDurationMs = results.Sum(r => r.Manifest.Audio.DurationMs),
            }));
        }
        var warnings = new List<string>();
        foreach (var (r, id) in results.Zip(sections))
        {
            if (r.Manifest.Missing.Count > 0) warnings.Add($"{id}: {r.Manifest.Missing.Count} segment(s) without an approved take were left out.");
            if (r.Manifest.Stale.Count > 0) warnings.Add($"{id}: {r.Manifest.Stale.Count} approved take(s) are out of date: {string.Join(", ", r.Manifest.Stale.Take(10))}");
        }
        var data = new
        {
            outputDir = outDir,
            index,
            sections = results.Select(r => new
            {
                manifest = r.ManifestPath, audio = r.AudioPath, srt = r.SrtPath, vtt = r.VttPath,
                r.Manifest.Audio.DurationMs, r.Manifest.Audio.SampleRate, segments = r.Manifest.Segments.Count, r.Manifest.Missing, r.Manifest.Stale,
                gainDb = r.Manifest.Processing.GainDb,
            }),
            totalDurationMs = results.Sum(r => r.Manifest.Audio.DurationMs),
        };
        return new CommandResult(data, $"Assembled {results.Count} section(s), {results.Sum(r => r.Manifest.Audio.DurationMs) / 1000.0:0.0} s total, into {outDir}.", warnings);
    }, ctx.Ct);

    public static Task<CommandResult> ExportClips(CommandContext ctx)
    {
        var s = Storage();
        var p = Open(ctx);
        var section = ctx.Args.Get("section");
        var outDir = ctx.Args.Get("out") ?? Path.Combine(p.Root, "exports", $"{section ?? "all"}_clips_{DateTime.Now:yyyyMMdd_HHmmss}");
        var r = ClipExporter.Export(p, Model(s, p), section, outDir, ctx.Args.Has("allow-missing"), ctx.Args.Has("overwrite"));
        var warnings = new List<string>();
        if (r.Missing.Count > 0) warnings.Add($"{r.Missing.Count} segment(s) without an approved take were skipped.");
        if (r.Stale.Count > 0) warnings.Add($"{r.Stale.Count} approved take(s) no longer match the current text/voice: {string.Join(", ", r.Stale.Take(10))}");
        return Task.FromResult(new CommandResult(r, $"Exported {r.Clips.Count} clip(s) ({r.Clips.Sum(c => c.DurationMs) / 1000.0:0.0} s) to {r.OutputDir}.", warnings));
    }
}

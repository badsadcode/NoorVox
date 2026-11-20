using System.Text;
using NoorVox.Core;

namespace NoorVox.Cli;

public sealed record CommandDef(string[] Path, string Summary, string Usage, string[] Options, Func<CommandContext, Task<CommandResult>> Run);

/// <summary>
/// The CLI contract. Keep docs/CLI_REFERENCE.md in sync with this table (tests assert every command is documented).
/// </summary>
public static class CommandTable
{
    static readonly string[] Global = ["json", "quiet", "non-interactive", "help"];

    public static readonly IReadOnlyList<CommandDef> All =
    [
        new(["version"], "Print version information.", "noorvox version [--json]", [], Commands.Version),
        new(["doctor"], "Check storage, models, voices and workers; report problems.", "noorvox doctor [--json]", [], Commands.Doctor),

        new(["storage", "show"], "Show configured and resolved storage paths.", "noorvox storage show [--json]", [], Commands.StorageShow),
        new(["storage", "set"], "Set storage paths (validated writable). Use 'default' to reset one.",
            "noorvox storage set [--models DIR] [--cache DIR] [--temp DIR] [--projects DIR] [--exports DIR] [--webview DIR] [--json]",
            ["models", "cache", "temp", "projects", "exports", "webview"], Commands.StorageSet),

        new(["storage", "audit"], "Sizes, free space and drives of every NoorVox location, plus NoorVox data found where the settings don't point (stray copies).",
            "noorvox storage audit [--json]", [], Commands.StorageAudit),
        new(["diagnostics", "bundle"], "Write a support zip: versions, settings, storage audit, models, workers, recent logs. Never scripts or audio.",
            "noorvox diagnostics bundle [--out FILE.zip] [--json]", ["out"], Commands.DiagnosticsBundle),

        new(["models", "list"], "List installed and catalog models with capabilities (speed, style, cloning, seed, GPU) and update status.", "noorvox models list [--json]", [], Commands.ModelsList),
        new(["models", "install"], "Download, verify and install a catalog model (plus its default voice packs and the engine pack it needs).",
            "noorvox models install <id[@version]> [--no-voices] [--no-engine] [--json]", ["no-voices", "no-engine"], Commands.ModelsInstall),

        new(["engines", "list"], "List engine packs (Qwen3-TTS runtime, ONNX Runtime CUDA) and whether they are installed.", "noorvox engines list [--json]", [], EngineCommands.List),
        new(["engines", "install"], "Install an engine pack: verified downloads, hash-locked Python packages, resumable.",
            "noorvox engines install <qwen-tts|onnxruntime-cuda> [--json]", [], EngineCommands.Install),
        new(["engines", "remove"], "Delete an installed engine pack (models and voices stay).", "noorvox engines remove <id> [--json]", [], EngineCommands.Remove),
        new(["gpu", "status"], "Kokoro's provider setting and the GPU the Qwen engine detected at install.", "noorvox gpu status [--json]", [], EngineCommands.GpuStatus),
        new(["gpu", "set"], "Run Kokoro on cpu (default) or cuda (needs the onnxruntime-cuda and qwen-tts engines; falls back to CPU).",
            "noorvox gpu set --kokoro cpu|cuda [--json]", ["kokoro"], EngineCommands.GpuSet),
        new(["models", "update"], "Install newer catalog versions of installed models (side by side). --check only reports.",
            "noorvox models update [--check] [--json]", ["check"], Commands.ModelsUpdate),
        new(["models", "import"], "Import a model folder already on disk; identity verified by catalog hashes.",
            "noorvox models import --from DIR [--json]", ["from"], Commands.ModelsImport),
        new(["models", "verify"], "Verify installed files (sha256 by default; --quick = sizes only).",
            "noorvox models verify [<id[@version]>] [--quick] [--json]", ["quick"], Commands.ModelsVerify),

        new(["voices", "list"], "List installed voices for a model.", "noorvox voices list [--model REF] [--language CODE] [--json]",
            ["model", "language"], Commands.VoicesList),
        new(["voices", "packs"], "List voice packs available/installed for a model.", "noorvox voices packs [--model REF] [--json]", ["model"], Commands.VoicesPacks),
        new(["voices", "blend"], "Create a new voice as a weighted mix of 2–4 installed voices (saved, hashed, immutable).",
            "noorvox voices blend --name NAME --mix ID:WEIGHT,ID:WEIGHT[,…] [--model REF] [--json]", ["name", "mix", "model"], Commands.VoicesBlend),
        new(["voices", "clone"], "Create a cloned voice profile from a 2-60 s WAV and its transcript. Requires --consent own-voice|speaker-permission.",
            "noorvox voices clone --name NAME --reference FILE.wav (--transcript TEXT | --transcript-file FILE) --consent own-voice|speaker-permission [--language auto|english|…] [--gender female|male] [--json]",
            ["name", "reference", "transcript", "transcript-file", "consent", "language", "gender"], EngineCommands.Clone),
        new(["voices", "remove-profile"], "Delete a cloned voice profile (its reference copy and consent record). Takes made with it stay.",
            "noorvox voices remove-profile --voice ID [--json]", ["voice"], EngineCommands.ProfileRemove),
        new(["voices", "install"], "Download and install a voice pack into an installed model.",
            "noorvox voices install <pack-id[@version]> [--model REF] [--json]", ["model"], Commands.VoicesInstall),

        new(["workers", "list"], "List NoorVox-managed workers with live state, PID, model and resources.", "noorvox workers list [--json]", [], Commands.WorkersList),
        new(["workers", "status"], "Status of one worker (state 'stopped' if not running).", "noorvox workers status [--worker ID] [--json]", ["worker"], Commands.WorkersStatus),
        new(["workers", "start"], "Start a persistent worker (optionally load a model). Exits after --idle-timeout seconds idle.",
            "noorvox workers start [--worker ID] [--model REF] [--idle-timeout SECONDS] [--json]", ["worker", "model", "idle-timeout"], Commands.WorkersStart),
        new(["workers", "load"], "Load a model into a running worker.", "noorvox workers load [--worker ID] [--model REF] [--json]", ["worker", "model"], Commands.WorkersLoad),
        new(["workers", "unload"], "Unload the model by recycling the worker process (frees all memory).",
            "noorvox workers unload [--worker ID] [--json]", ["worker"], Commands.WorkersUnload),
        new(["workers", "stop"], "Stop one worker or all (--all). Graceful by default; --force terminates.",
            "noorvox workers stop (--worker ID | --all) [--force] [--timeout SECONDS] [--json]", ["worker", "all", "force", "graceful", "timeout"], Commands.WorkersStop),

        new(["projects", "create"], "Create a project folder (kinds: documentary, audiobook, dialogue, narration, education, custom).",
            "noorvox projects create --path DIR --title TITLE [--kind KIND] [--voice ID] [--model REF] [--json]", ["path", "title", "kind", "voice", "model"], ProjectCommands.Create),
        new(["projects", "import"], "Import a script: .md/.txt (headings → sections/scenes, paragraphs → segments) or dialogue .csv/.json (line_id, character, text).",
            "noorvox projects import --project DIR --file FILE [--format md|txt|csv|json] [--json]", ["project", "file", "format"], ProjectCommands.Import),
        new(["projects", "show"], "Project summary with per-status counts; --segments adds the full structure and every segment's status.",
            "noorvox projects show --project DIR [--segments] [--no-text] [--json]", ["project", "segments", "no-text"], ProjectCommands.Show),
        new(["projects", "validate"], "Check ids, files, speakers, voices and approved takes.", "noorvox projects validate --project DIR [--json]", ["project"], ProjectCommands.Validate),
        new(["projects", "rename"], "Change a project's title (the folder name stays).", "noorvox projects rename --project DIR --title TITLE [--json]", ["project", "title"], ProjectCommands.Rename),
        new(["projects", "duplicate"], "Copy a project (scripts, takes, approvals) to a new empty folder with a new id; job history and exports stay behind.",
            "noorvox projects duplicate --project DIR --path NEWDIR [--title TITLE] [--json]", ["project", "path", "title"], ProjectCommands.Duplicate),
        new(["projects", "split"], "Split a segment in two at a character position or before some text. The first part keeps the id.",
            "noorvox projects split --project DIR --segment ID (--at INDEX | --before TEXT) [--json]", ["project", "segment", "at", "before"], ProjectCommands.Split),
        new(["projects", "merge"], "Merge a segment with the next one in the same scene (the second one's takes stay on disk).",
            "noorvox projects merge --project DIR --segment ID [--json]", ["project", "segment"], ProjectCommands.Merge),
        new(["pronunciation", "list"], "List the project's pronunciation rules (text replacements applied before synthesis).",
            "noorvox pronunciation list --project DIR [--json]", ["project"], ProjectCommands.PronunciationList),
        new(["pronunciation", "set"], "Add or change a rule: --match NoorEngine --replace \"Noor Engine\". Reports how many segments change.",
            "noorvox pronunciation set --project DIR --match WORD --replace TEXT [--case-sensitive] [--partial] [--json]",
            ["project", "match", "replace", "case-sensitive", "partial"], ProjectCommands.PronunciationSet),
        new(["pronunciation", "remove"], "Remove the rule for a word.", "noorvox pronunciation remove --project DIR --match WORD [--json]", ["project", "match"], ProjectCommands.PronunciationRemove),
        new(["projects", "cast"], "Set the project's default voice (and model), or a speaker's voice (--speaker), with optional speed or style.",
            "noorvox projects cast --project DIR [--speaker ID] --voice ID [--speed X] [--style TEXT] [--model REF] [--json]", ["project", "speaker", "voice", "speed", "style", "model"], ProjectCommands.Cast),

        new(["generate"], "Generate takes for a project, section, scene or segments as a resumable job. --approve approves new takes where none is approved.",
            "noorvox generate --project DIR [--section ID] [--scene ID] [--segment ID[,ID…]] [--policy missing|all] [--approve] [--worker ID] [--keep-worker] [--json]",
            ["project", "section", "scene", "segment", "policy", "approve", "worker", "keep-worker"], ProjectCommands.Generate),
        new(["jobs", "list"], "List generation jobs (interrupted jobs are detected).", "noorvox jobs list --project DIR [--json]", ["project"], ProjectCommands.JobsList),
        new(["jobs", "status"], "One job with per-segment results.", "noorvox jobs status --project DIR --job ID [--json]", ["project", "job"], ProjectCommands.JobsStatus),
        new(["jobs", "resume"], "Continue an interrupted or partly failed job at the first unfinished segment.",
            "noorvox jobs resume --project DIR --job ID [--worker ID] [--keep-worker] [--json]", ["project", "job", "worker", "keep-worker"], ProjectCommands.Resume),

        new(["takes", "list"], "List a segment's takes (approved, matching current settings, paths, durations).",
            "noorvox takes list --project DIR --segment ID [--json]", ["project", "segment"], ProjectCommands.TakesList),
        new(["takes", "approve"], "Approve a take (approved takes are immutable; approval is a pointer).",
            "noorvox takes approve --project DIR --segment ID (--take ID | --latest) [--json]", ["project", "segment", "take", "latest"], ProjectCommands.Approve),
        new(["takes", "unapprove"], "Clear a segment's approval (no files are deleted).", "noorvox takes unapprove --project DIR --segment ID [--json]", ["project", "segment"], ProjectCommands.Unapprove),

        new(["export", "clips"], "Copy approved takes out with id-based names plus clips.json (durations, text, speaker).",
            "noorvox export clips --project DIR [--section ID] [--out DIR] [--allow-missing] [--overwrite] [--json]",
            ["project", "section", "out", "allow-missing", "overwrite"], ProjectCommands.ExportClips),

        new(["export", "assemble"], "Join approved takes into one WAV per section with structured pauses, plus SRT/VTT and a timing manifest for video.",
            "noorvox export assemble --project DIR [--section ID] [--rate 24000|44100|48000] [--normalize] [--peak-dbfs DB] [--no-subtitles] [--out DIR] [--allow-missing] [--overwrite] [--json]",
            ["project", "section", "rate", "normalize", "peak-dbfs", "no-subtitles", "out", "allow-missing", "overwrite"], ProjectCommands.Assemble),

        new(["synth"], "Synthesize text to a WAV file and report measured duration and provenance.",
            "noorvox synth (--text TEXT | --text-file FILE | --stdin) [--out FILE.wav] [--voice ID] [--speed X] [--style TEXT] [--seed N] [--language NAME] [--model REF] [--worker ID] [--keep-worker] [--overwrite] [--manifest] [--json]",
            ["text", "text-file", "stdin", "out", "voice", "speed", "style", "seed", "language", "model", "worker", "keep-worker", "overwrite", "manifest"], Commands.Synth),
    ];

    public static (CommandDef? Command, List<string> Remaining) Match(List<string> words)
    {
        foreach (var c in All.OrderByDescending(c => c.Path.Length))
        {
            if (words.Count >= c.Path.Length && c.Path.Select((p, i) => p.Equals(words[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
                return (c, words.Skip(c.Path.Length).ToList());
        }
        return (null, words);
    }

    public static void ValidateOptions(CommandDef c, ParsedArgs args)
    {
        var unknown = args.Options.Keys.Where(k => !Global.Contains(k, StringComparer.OrdinalIgnoreCase) && !c.Options.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments,
                $"Unknown option(s) for '{string.Join(' ', c.Path)}': {string.Join(", ", unknown.Select(u => "--" + u))}. Usage: {c.Usage}");
    }

    public static (string Text, object Data) Help(CommandDef? command, List<string> words)
    {
        var list = command is not null ? [command] : All.Where(c => words.Count == 0 || c.Path[0].Equals(words[0], StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) list = All.ToList();
        var sb = new StringBuilder();
        if (command is null)
        {
            sb.AppendLine($"NoorVox {NoorVoxInfo.Version} — local voiceover CLI (offline Kokoro and Qwen3-TTS).");
            if (words.Count > 0 && !All.Any(c => c.Path[0].Equals(words[0], StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine($"Unknown command: {string.Join(' ', words)}");
            sb.AppendLine();
        }
        foreach (var c in list)
        {
            sb.AppendLine($"  {c.Usage}");
            sb.AppendLine($"      {c.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("Global options: --json (single JSON envelope on stdout), --quiet (no progress on stderr), --non-interactive (never prompts; NoorVox CLI never prompts anyway).");
        sb.AppendLine("Docs: docs/CLI_REFERENCE.md and docs/AI_INTEGRATION.md");
        var data = new
        {
            commands = list.Select(c => new { command = string.Join(' ', c.Path), c.Summary, c.Usage, options = c.Options }).ToList(),
            globalOptions = Global,
            exitCodes = Enum.GetValues<ExitCode>().ToDictionary(e => (int)e, e => e.ToString()),
        };
        return (sb.ToString(), data);
    }
}

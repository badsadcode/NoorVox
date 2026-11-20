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

        new(["models", "list"], "List installed models and catalog models (with update status).", "noorvox models list [--json]", [], Commands.ModelsList),
        new(["models", "install"], "Download, verify and install a catalog model (plus its default voice packs).",
            "noorvox models install <id[@version]> [--no-voices] [--json]", ["no-voices"], Commands.ModelsInstall),
        new(["models", "update"], "Install newer catalog versions of installed models (side by side). --check only reports.",
            "noorvox models update [--check] [--json]", ["check"], Commands.ModelsUpdate),
        new(["models", "import"], "Import a model folder already on disk; identity verified by catalog hashes.",
            "noorvox models import --from DIR [--json]", ["from"], Commands.ModelsImport),
        new(["models", "verify"], "Verify installed files (sha256 by default; --quick = sizes only).",
            "noorvox models verify [<id[@version]>] [--quick] [--json]", ["quick"], Commands.ModelsVerify),

        new(["voices", "list"], "List installed voices for a model.", "noorvox voices list [--model REF] [--language CODE] [--json]",
            ["model", "language"], Commands.VoicesList),
        new(["voices", "packs"], "List voice packs available/installed for a model.", "noorvox voices packs [--model REF] [--json]", ["model"], Commands.VoicesPacks),
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

        new(["synth"], "Synthesize text to a WAV file and report measured duration and provenance.",
            "noorvox synth (--text TEXT | --text-file FILE | --stdin) [--out FILE.wav] [--voice ID] [--speed X] [--model REF] [--worker ID] [--keep-worker] [--overwrite] [--manifest] [--json]",
            ["text", "text-file", "stdin", "out", "voice", "speed", "model", "worker", "keep-worker", "overwrite", "manifest"], Commands.Synth),
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
            sb.AppendLine($"NoorVox {NoorVoxInfo.Version} — local voiceover CLI (offline Kokoro TTS).");
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

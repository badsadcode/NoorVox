using System.Text;
using System.Text.Json;
using NoorVox.Core;

namespace NoorVox.Cli;

/// <summary>Parsed command line: verb path words, positional args and --options.</summary>
public sealed class ParsedArgs
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "non-interactive", "help", "quiet", "force", "all", "keep-worker", "overwrite", "check",
        "no-voices", "stdin", "graceful", "manifest", "full", "quick", "version", "approve", "latest", "allow-missing", "no-text", "segments",
    };

    public List<string> Words { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ParsedArgs Parse(string[] args)
    {
        var p = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var body = a[2..];
                var eq = body.IndexOf('=');
                if (eq >= 0) { p.Options[body[..eq]] = body[(eq + 1)..]; continue; }
                if (Flags.Contains(body)) { p.Options[body] = null; continue; }
                if (i + 1 >= args.Length)
                    throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Option --{body} needs a value.");
                p.Options[body] = args[++i];
            }
            else if (a is "-h" or "-?") p.Options["help"] = null;
            else p.Words.Add(a);
        }
        return p;
    }

    public bool Has(string name) => Options.ContainsKey(name);
    public string? Get(string name) => Options.TryGetValue(name, out var v) ? v : null;

    public string Require(string name) => Get(name) is { Length: > 0 } v ? v
        : throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"Missing required option --{name}.");

    public int GetInt(string name, int fallback)
    {
        var v = Get(name);
        if (v is null) return fallback;
        return int.TryParse(v, out var i) && i >= 0 ? i
            : throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"--{name} must be a non-negative integer.");
    }

    public double GetDouble(string name, double fallback)
    {
        var v = Get(name);
        if (v is null) return fallback;
        return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
            : throw new NoorVoxException(ErrorCodes.InvalidArguments, ExitCode.InvalidArguments, $"--{name} must be a number (use '.' as decimal separator).");
    }
}

public sealed record CommandResult(object? Data, string Message, IReadOnlyList<string>? Warnings = null);

/// <summary>Everything a command needs. Output discipline: stdout = result only; progress/logs → stderr.</summary>
public sealed class CommandContext(ParsedArgs args, CancellationToken ct)
{
    public ParsedArgs Args { get; } = args;
    public CancellationToken Ct { get; } = ct;
    public bool Json => Args.Has("json");
    public bool Quiet => Args.Has("quiet");
    public List<string> Positionals { get; } = [];

    DateTime _lastProgress = DateTime.MinValue;
    bool _progressLineOpen;

    /// <summary>Human progress on stderr (also in --json mode; stdout stays pure JSON). Throttled.</summary>
    public void Progress(string text, bool force = false)
    {
        if (Quiet) return;
        // Redirected stderr (agents, logs) gets a line every 2 s instead of a live-updating line.
        if (!force && (DateTime.UtcNow - _lastProgress).TotalMilliseconds < (Console.IsErrorRedirected ? 2000 : 200)) return;
        _lastProgress = DateTime.UtcNow;
        if (Console.IsErrorRedirected) Console.Error.WriteLine(text);
        else { Console.Error.Write("\r" + text.PadRight(Math.Min(Console.BufferWidth - 1, 100))); _progressLineOpen = true; }
    }

    public void EndProgress()
    {
        if (_progressLineOpen) { Console.Error.WriteLine(); _progressLineOpen = false; }
    }

    public void Info(string text)
    {
        if (Quiet) return;
        EndProgress();
        Console.Error.WriteLine(text);
    }
}

public static class Output
{
    public static void WriteEnvelope(string command, bool success, string code, string message, object? data, IReadOnlyList<string>? warnings)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["schemaVersion"] = NoorVoxInfo.SchemaVersion,
            ["tool"] = "noorvox",
            ["toolVersion"] = NoorVoxInfo.Version,
            ["command"] = command,
            ["success"] = success,
            ["code"] = code,
            ["message"] = message,
            ["data"] = data,
            ["warnings"] = warnings ?? [],
        };
        Console.Out.WriteLine(NoorVox.Core.Json.Serialize(envelope));
    }

    /// <summary>Readable rendering of any result object for humans (non --json mode).</summary>
    public static void WriteHuman(string message, object? data, IReadOnlyList<string>? warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(message);
        if (data is not null)
        {
            var el = JsonSerializer.SerializeToElement(data, NoorVox.Core.Json.Options);
            Render(sb, el, 1);
        }
        foreach (var w in warnings ?? []) sb.AppendLine($"warning: {w}");
        Console.Out.Write(sb.ToString());
    }

    static void Render(StringBuilder sb, JsonElement el, int depth)
    {
        var pad = new string(' ', depth * 2);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && HasContent(prop.Value))
                    {
                        sb.AppendLine($"{pad}{prop.Name}:");
                        Render(sb, prop.Value, depth + 1);
                    }
                    else if (prop.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                        sb.AppendLine($"{pad}{prop.Name}: {Scalar(prop.Value)}");
                }
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine($"{pad}- [{i}]");
                        Render(sb, item, depth + 1);
                    }
                    else sb.AppendLine($"{pad}- {Scalar(item)}");
                    i++;
                }
                break;
            default:
                sb.AppendLine($"{pad}{Scalar(el)}");
                break;
        }
    }

    static bool HasContent(JsonElement el) => el.ValueKind == JsonValueKind.Object ? el.EnumerateObject().Any() : el.GetArrayLength() > 0;
    static string Scalar(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText();
}

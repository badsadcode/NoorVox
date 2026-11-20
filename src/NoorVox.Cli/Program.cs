using System.Text;
using NoorVox.Cli;
using NoorVox.Core;

Console.OutputEncoding = new UTF8Encoding(false);
// Output is read by scripts and agents: numbers and dates must not depend on the Windows locale ("34.6", not "34,6").
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
Console.InputEncoding = new UTF8Encoding(false);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ParsedArgs parsed;
try { parsed = ParsedArgs.Parse(args); }
catch (NoorVoxException ex)
{
    Output.WriteEnvelope("", false, ex.Code, ex.Message, null, null);
    return (int)ex.ExitCode;
}

if (parsed.Has("version") && parsed.Words.Count == 0) { parsed.Words.Add("version"); parsed.Options.Remove("version"); }
var (command, rest) = CommandTable.Match(parsed.Words);
bool json = parsed.Has("json");

if (command is null || parsed.Has("help"))
{
    var help = CommandTable.Help(command, parsed.Words);
    if (json) Output.WriteEnvelope("help", true, "ok", "NoorVox CLI help.", help.Data, null);
    else Console.Out.Write(help.Text);
    // Unknown command = usage error; explicit help or bare `noorvox` = success.
    return command is null && parsed.Words.Count > 0 && !parsed.Has("help") ? (int)ExitCode.InvalidArguments : 0;
}

var name = string.Join(' ', command.Path);
var ctx = new CommandContext(parsed, cts.Token);
ctx.Positionals.AddRange(rest);
try
{
    CommandTable.ValidateOptions(command, parsed);
    var result = await command.Run(ctx);
    ctx.EndProgress();
    if (json) Output.WriteEnvelope(name, true, "ok", result.Message, result.Data, result.Warnings);
    else Output.WriteHuman(result.Message, result.Data, result.Warnings);
    return 0;
}
catch (Exception ex)
{
    ctx.EndProgress();
    var (code, exit, message, details) = ex switch
    {
        NoorVoxException n => (n.Code, n.ExitCode, n.Message, n.Details),
        OperationCanceledException when cts.IsCancellationRequested => (ErrorCodes.Cancelled, ExitCode.Cancelled, "Cancelled by user.", null),
        UnauthorizedAccessException u => (ErrorCodes.PermissionDenied, ExitCode.IoError, u.Message, null),
        IOException io => (ErrorCodes.IoError, ExitCode.IoError, io.Message, null),
        _ => (ErrorCodes.InternalError, ExitCode.InternalError, $"Unexpected error: {ex.Message}", (object?)new { type = ex.GetType().FullName, stack = ex.StackTrace }),
    };
    if (json) Output.WriteEnvelope(name, false, code, message, details, null);
    else
    {
        Console.Error.WriteLine($"error ({code}): {message}");
        if (details is not null) Console.Error.WriteLine(Json.Serialize(details));
    }
    return (int)exit;
}

// NoorVox.Worker — isolated inference process. Launched only by NoorVox.Core's WorkerSupervisor.
// Exit codes: 0 clean shutdown, 2 bad arguments, 3 another worker with this id is alive, 4 missing token, 1 crash.
using NoorVox.Backends.Kokoro;
using NoorVox.Backends.Qwen;
using NoorVox.Core;
using NoorVox.Core.Backends;
using NoorVox.Core.Workers;
using NoorVox.Worker;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var workerId = Arg("--worker-id");
var pipe = Arg("--pipe");
var record = Arg("--record");
var logPath = Arg("--log");
if (workerId is null || pipe is null || record is null || logPath is null || !int.TryParse(Arg("--idle-timeout") ?? "0", out var idle))
{
    Console.Error.WriteLine("NoorVox.Worker is started by noorvox / NoorVox Studio, not directly.");
    return 2;
}

var token = Environment.GetEnvironmentVariable(WorkerSupervisor.TokenEnvVar);
Environment.SetEnvironmentVariable(WorkerSupervisor.TokenEnvVar, null);
if (string.IsNullOrEmpty(token)) return 4;

using var log = new WorkerLog(logPath);
// One live worker per id, enforced by the OS even if two supervisors race.
using var mutex = new Mutex(initiallyOwned: true, $"Local\\NoorVox.Worker.{workerId}", out bool createdNew);
if (!createdNew)
{
    log.Write($"another worker '{workerId}' is already alive; exiting");
    return 3;
}

try
{
    var host = new WorkerHost(workerId, pipe, token, record, idle, model => model.Backend switch
    {
        "kokoro-onnx" => new KokoroBackend(log.Write),
        "qwen3-tts" => new QwenBackend(log.Write),
        var other => throw new NoorVoxException(ErrorCodes.Unsupported, ExitCode.WorkerError, $"This NoorVox has no '{other}' backend; update NoorVox."),
    }, log);
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    log.Write($"FATAL {ex}");
    return 1;
}

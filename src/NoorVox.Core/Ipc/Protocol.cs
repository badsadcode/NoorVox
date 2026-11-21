using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NoorVox.Core.Ipc;

/// <summary>
/// Supervisor ⇄ worker protocol: one request per pipe connection, newline-delimited compact JSON.
/// Pipes are created with <see cref="PipeOptions.CurrentUserOnly"/>; every request also carries the per-launch token.
/// </summary>
public static class IpcMethods
{
    public const string Status = "status";
    public const string Load = "load";
    public const string Synthesize = "synthesize";
    public const string Shutdown = "shutdown";
}

public sealed class IpcRequest
{
    public string Token { get; set; } = "";
    public string Id { get; set; } = "";
    public string Method { get; set; } = "";
    public JsonElement? Params { get; set; }
}

public sealed class IpcResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public JsonElement? Result { get; set; }
    public IpcError? Error { get; set; }
}

public sealed record IpcError(string Code, string Message);

public sealed record LoadParams(string ModelDirectory, Backends.BackendLoadOptions? Options = null);
public sealed record SynthesizeParams(string Text, string VoiceId, double Speed, string OutputPath, bool Wait = true,
    string? Style = null, long? Seed = null, string? Language = null, double? Emotion = null, double? Pace = null);
public sealed record ShutdownParams(bool Drain = true);

public sealed record LoadedModelInfo(string Id, string Version, string ModelSha256, string Directory, DateTimeOffset LoadedAt, long LoadMs);
public sealed record CurrentJobInfo(string RequestId, string Kind, DateTimeOffset StartedAt, int TextChars, string? VoiceId);

/// <summary>What a worker reports about itself.</summary>
public sealed class WorkerRuntimeStatus
{
    public string WorkerId { get; set; } = "";
    public int Pid { get; set; }
    /// <summary><c>idle</c> (no model) · <c>loading</c> · <c>ready</c> · <c>generating</c> · <c>stopping</c>.</summary>
    public string State { get; set; } = "idle";
    public DateTimeOffset StartedAt { get; set; }
    public string Backend { get; set; } = "";
    public string BackendVersion { get; set; } = "";
    public string ExecutionProvider { get; set; } = "";
    public LoadedModelInfo? LoadedModel { get; set; }
    public CurrentJobInfo? CurrentJob { get; set; }
    public long JobsCompleted { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int IdleTimeoutSeconds { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public long CpuTimeMs { get; set; }
    public DateTimeOffset SampledAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>GPU memory as the engine reports it; null when the engine can't measure it.</summary>
    public Backends.GpuMemoryInfo? Gpu { get; set; }
    /// <summary>A helper process (Qwen's Python engine) with its own memory footprint.</summary>
    public Backends.SidecarInfo? Sidecar { get; set; }
    public string? ProviderNote { get; set; }
    /// <summary>While <see cref="State"/> is <c>loading</c>: which model, since when, and the engine's current stage.</summary>
    public LoadingInfo? Loading { get; set; }
}

public sealed record LoadingInfo(string ModelId, string ModelVersion, DateTimeOffset StartedAt, string? Stage);

/// <summary>Raised when the worker cannot be reached at all (no pipe, timeout, broken connection).</summary>
public sealed class WorkerUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

public static class WorkerClient
{
    public static async Task<T> CallAsync<T>(string pipeName, string token, string method, object? parameters,
        TimeSpan connectTimeout, TimeSpan? responseTimeout, CancellationToken ct)
    {
        var request = new IpcRequest
        {
            Token = token,
            Id = Guid.NewGuid().ToString("N")[..12],
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, Json.Compact),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? line;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, cts.Token);
            if (responseTimeout is { } rt) cts.CancelAfter(rt);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json.Compact).AsMemory(), cts.Token);
            line = await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkerUnreachableException($"Worker pipe '{pipeName}' timed out ({method}).");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            throw new WorkerUnreachableException($"Worker pipe '{pipeName}' unavailable ({method}): {ex.Message}", ex);
        }

        if (line is null) throw new WorkerUnreachableException($"Worker closed the connection during '{method}'.");
        var response = JsonSerializer.Deserialize<IpcResponse>(line, Json.Compact)
            ?? throw new WorkerUnreachableException("Worker sent an empty response.");
        if (!response.Ok)
        {
            var err = response.Error ?? new IpcError(ErrorCodes.WorkerError, "Unknown worker error.");
            throw new NoorVoxException(err.Code, ExitCodeFor(err.Code), err.Message);
        }
        return response.Result is { } r ? r.Deserialize<T>(Json.Compact)! : default!;
    }

    public static ExitCode ExitCodeFor(string code) => code switch
    {
        ErrorCodes.InvalidArguments => ExitCode.InvalidArguments,
        ErrorCodes.InvalidInput => ExitCode.InvalidInput,
        ErrorCodes.ModelNotInstalled or ErrorCodes.VoiceNotFound or ErrorCodes.UnknownModel or ErrorCodes.EngineNotInstalled => ExitCode.ModelMissing,
        ErrorCodes.Unsupported => ExitCode.InvalidArguments,
        ErrorCodes.GpuOutOfMemory => ExitCode.SynthesisFailed,
        ErrorCodes.WorkerBusy or ErrorCodes.WorkerAlreadyRunning => ExitCode.WorkerBusy,
        ErrorCodes.SynthesisFailed => ExitCode.SynthesisFailed,
        ErrorCodes.IoError or ErrorCodes.PermissionDenied or ErrorCodes.OutputExists => ExitCode.IoError,
        ErrorCodes.Cancelled => ExitCode.Cancelled,
        _ => ExitCode.WorkerError,
    };
}

namespace NoorVox.Core;

/// <summary>
/// Process exit codes. Stable contract — documented in docs/CLI_REFERENCE.md. Never renumber.
/// </summary>
public enum ExitCode
{
    Ok = 0,
    InternalError = 1,
    InvalidArguments = 2,
    ConfigError = 3,
    ModelMissing = 4,
    InvalidInput = 5,
    WorkerBusy = 6,
    WorkerError = 7,
    SynthesisFailed = 8,
    IoError = 9,
    DownloadFailed = 10,
    Cancelled = 11,
    NotFound = 12,
}

/// <summary>Stable machine-readable error codes (the JSON envelope's <c>code</c>).</summary>
public static class ErrorCodes
{
    public const string InvalidArguments = "invalid_arguments";
    public const string ConfigError = "config_error";
    public const string ModelNotInstalled = "model_not_installed";
    public const string VoiceNotFound = "voice_not_found";
    public const string UnknownModel = "unknown_model";
    public const string InvalidInput = "invalid_input";
    public const string WorkerBusy = "worker_busy";
    public const string WorkerAlreadyRunning = "worker_already_running";
    public const string WorkerNotRunning = "worker_not_running";
    public const string WorkerStartFailed = "worker_start_failed";
    public const string WorkerUnresponsive = "worker_unresponsive";
    public const string WorkerStopTimeout = "worker_stop_timeout";
    public const string WorkerError = "worker_error";
    public const string ModelLoadFailed = "model_load_failed";
    public const string SynthesisFailed = "synthesis_failed";
    public const string OutputExists = "output_exists";
    public const string IoError = "io_error";
    public const string PermissionDenied = "permission_denied";
    public const string DownloadFailed = "download_failed";
    public const string VerificationFailed = "verification_failed";
    public const string Cancelled = "cancelled";
    public const string InternalError = "internal_error";
}

/// <summary>An expected failure with a stable code, exit code and optional machine-readable details.</summary>
public sealed class NoorVoxException(string code, ExitCode exitCode, string message, object? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public ExitCode ExitCode { get; } = exitCode;
    public object? Details { get; } = details;
}

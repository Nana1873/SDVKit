namespace SdvKit.Cli.LiveLab;

internal sealed record OwnedProcessIdentity(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    string ExecutablePath);

internal sealed record LabProcessStartSpec(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutputPath,
    string StandardErrorPath);

internal interface ILabProcessHost
{
    LabProcessStartResult Start(LabProcessStartSpec specification);

    LabProcessInspectResult Inspect(OwnedProcessIdentity expected);

    LabProcessWaitResult WaitForExit(
        OwnedProcessIdentity expected,
        TimeSpan timeout);

    LabProcessCloseResult RequestCloseAndWait(
        OwnedProcessIdentity expected,
        TimeSpan timeout);
}

internal enum LabProcessStartStatus
{
    Started,
    ExitedBeforeIdentityVerification,
    IdentityMismatch,
    Unreadable,
    Failed,
}

internal sealed record LabProcessStartResult(
    LabProcessStartStatus Status,
    OwnedProcessIdentity? Identity = null,
    string? Error = null);

internal enum LabProcessInspectStatus
{
    Running,
    Exited,
    IdentityMismatch,
    Unreadable,
}

internal sealed record LabProcessInspectResult(
    LabProcessInspectStatus Status,
    string? Error = null);

internal enum LabProcessWaitStatus
{
    Exited,
    IdentityMismatch,
    Unreadable,
    TimedOut,
}

internal sealed record LabProcessWaitResult(
    LabProcessWaitStatus Status,
    string? Error = null);

internal enum LabProcessCloseStatus
{
    Closed,
    AlreadyExited,
    IdentityMismatch,
    Unreadable,
    NoCloseableWindow,
    MultipleCloseableWindows,
    CloseRequestFailed,
    TimedOut,
}

internal sealed record LabProcessCloseResult(
    LabProcessCloseStatus Status,
    string? Error = null);

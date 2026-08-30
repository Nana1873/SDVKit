using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.Cli.LiveLab;

internal sealed class WindowsLabProcessHost : ILabProcessHost
{
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const uint WindowMessageClose = 0x0010;
    private const uint GetWindowOwner = 4;
    private const uint WindowPollIntervalMilliseconds = 50;
    private const uint FailedStartAbortExitCode = 3;
    private const uint FailedStartAbortWaitMilliseconds = 5000;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;

    public LabProcessStartResult Start(LabProcessStartSpec specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        EnsureWindows();

        ProcessStartInfo startInfo = CreateStartInfo(specification);
        LabProcessStartSpec normalizedSpecification = specification with
        {
            ExecutablePath = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            StandardOutputPath = NormalizeFullyQualifiedPath(
                specification.StandardOutputPath,
                nameof(specification.StandardOutputPath)),
            StandardErrorPath = NormalizeFullyQualifiedPath(
                specification.StandardErrorPath,
                nameof(specification.StandardErrorPath)),
        };
        WindowsProcessLaunchResult launched;
        try
        {
            launched = WindowsProcessLauncher.Start(normalizedSpecification);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new LabProcessStartResult(
                LabProcessStartStatus.Failed,
                Error: exception.Message);
        }

        using (launched)
        {
            SafeProcessHandle handle = launched.ProcessHandle;

            RunningState running = ReadRunningState(handle, out string? runningError);
            if (running == RunningState.Exited)
            {
                return new LabProcessStartResult(
                    LabProcessStartStatus.ExitedBeforeIdentityVerification,
                    Error: "The started process exited before its identity could be verified.");
            }

            if (running == RunningState.Unreadable)
            {
                return AbortUnverifiedStart(
                    handle,
                    launched.ProcessId,
                    runningError ?? "The created process state could not be read.");
            }

            if (!TryReadIdentity(
                    handle,
                    launched.ProcessId,
                    out OwnedProcessIdentity identity,
                    out string? error))
            {
                return AbortUnverifiedStart(
                    handle,
                    launched.ProcessId,
                    error ?? "The created process identity could not be read.");
            }

            running = ReadRunningState(handle, out runningError);
            if (running == RunningState.Exited)
            {
                return new LabProcessStartResult(
                    LabProcessStartStatus.ExitedBeforeIdentityVerification,
                    identity,
                    "The started process exited while its identity was being verified.");
            }

            if (running == RunningState.Unreadable)
            {
                return new LabProcessStartResult(
                    LabProcessStartStatus.Unreadable,
                    identity,
                    runningError);
            }

            if (!ExecutableIdentity.AreEquivalent(
                    identity.ExecutablePath,
                    normalizedSpecification.ExecutablePath))
            {
                return new LabProcessStartResult(
                    LabProcessStartStatus.IdentityMismatch,
                    identity,
                    $"Started PID {launched.ProcessId} does not run the requested executable identity.");
            }

            return new LabProcessStartResult(LabProcessStartStatus.Started, identity);
        }
    }

    public LabProcessInspectResult Inspect(OwnedProcessIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureWindows();

        if (!IsValid(expected, out string? validationError))
        {
            return new LabProcessInspectResult(
                LabProcessInspectStatus.Unreadable,
                validationError);
        }

        SafeProcessHandle handle = OpenProcess(
            Synchronize | ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)expected.ProcessId));
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return IsMissingProcessError(error)
                ? new LabProcessInspectResult(LabProcessInspectStatus.Exited)
                : new LabProcessInspectResult(
                    LabProcessInspectStatus.Unreadable,
                    DescribeWindowsError("Windows could not inspect the owned process", error));
        }

        using (handle)
        {
            Verification verification = VerifyExpectedIdentity(
                handle,
                expected,
                out string? verificationError);
            return verification switch
            {
                Verification.Running => new LabProcessInspectResult(LabProcessInspectStatus.Running),
                Verification.Exited => new LabProcessInspectResult(LabProcessInspectStatus.Exited),
                Verification.IdentityMismatch => new LabProcessInspectResult(
                    LabProcessInspectStatus.IdentityMismatch,
                    "The PID no longer has the recorded start time and executable identity."),
                _ => new LabProcessInspectResult(
                    LabProcessInspectStatus.Unreadable,
                    verificationError),
            };
        }
    }

    public LabProcessWaitResult WaitForExit(
        OwnedProcessIdentity expected,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureWindows();
        uint timeoutMilliseconds = GetBoundedTimeoutMilliseconds(timeout);

        if (!IsValid(expected, out string? validationError))
        {
            return new LabProcessWaitResult(
                LabProcessWaitStatus.Unreadable,
                validationError);
        }

        SafeProcessHandle handle = OpenProcess(
            Synchronize | ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)expected.ProcessId));
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return IsMissingProcessError(error)
                ? new LabProcessWaitResult(LabProcessWaitStatus.Exited)
                : new LabProcessWaitResult(
                    LabProcessWaitStatus.Unreadable,
                    DescribeWindowsError(
                        "Windows could not open the owned process for exit wait",
                        error));
        }

        using (handle)
        {
            Verification verification = VerifyExpectedIdentity(
                handle,
                expected,
                out string? verificationError);
            if (verification != Verification.Running)
            {
                return verification switch
                {
                    Verification.Exited => new LabProcessWaitResult(
                        LabProcessWaitStatus.Exited),
                    Verification.IdentityMismatch => new LabProcessWaitResult(
                        LabProcessWaitStatus.IdentityMismatch,
                        "The PID no longer has the recorded start time and executable identity."),
                    _ => new LabProcessWaitResult(
                        LabProcessWaitStatus.Unreadable,
                        verificationError),
                };
            }

            uint wait = WaitForSingleObject(handle, timeoutMilliseconds);
            return wait switch
            {
                WaitObject0 => new LabProcessWaitResult(LabProcessWaitStatus.Exited),
                WaitTimeout => new LabProcessWaitResult(LabProcessWaitStatus.TimedOut),
                WaitFailed => new LabProcessWaitResult(
                    LabProcessWaitStatus.Unreadable,
                    DescribeWindowsError(
                        "Windows could not wait for the exact owned process to exit",
                        Marshal.GetLastWin32Error())),
                _ => new LabProcessWaitResult(
                    LabProcessWaitStatus.Unreadable,
                    $"Windows returned unexpected process wait result {wait}."),
            };
        }
    }

    public LabProcessCloseResult RequestCloseAndWait(
        OwnedProcessIdentity expected,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureWindows();
        uint timeoutMilliseconds = GetBoundedTimeoutMilliseconds(timeout);

        if (!IsValid(expected, out string? validationError))
        {
            return new LabProcessCloseResult(
                LabProcessCloseStatus.Unreadable,
                validationError);
        }

        Stopwatch operationTimer = Stopwatch.StartNew();
        SafeProcessHandle handle = OpenProcess(
            Synchronize | ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)expected.ProcessId));
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return IsMissingProcessError(error)
                ? new LabProcessCloseResult(LabProcessCloseStatus.AlreadyExited)
                : new LabProcessCloseResult(
                    LabProcessCloseStatus.Unreadable,
                    DescribeWindowsError("Windows could not open the owned process for close", error));
        }

        using (handle)
        {
            Verification verification = VerifyExpectedIdentity(
                handle,
                expected,
                out string? verificationError);
            if (verification != Verification.Running)
            {
                return verification switch
                {
                    Verification.Exited => new LabProcessCloseResult(LabProcessCloseStatus.AlreadyExited),
                    Verification.IdentityMismatch => new LabProcessCloseResult(
                        LabProcessCloseStatus.IdentityMismatch,
                        "The PID no longer has the recorded start time and executable identity; WM_CLOSE was not sent."),
                    _ => new LabProcessCloseResult(
                        LabProcessCloseStatus.Unreadable,
                        verificationError),
                };
            }

            IntPtr window;
            while (true)
            {
                if (!TryFindCloseableWindows(
                        expected.ProcessId,
                        out IReadOnlyList<IntPtr> windows,
                        out string? enumerationError))
                {
                    return new LabProcessCloseResult(
                        LabProcessCloseStatus.Unreadable,
                        enumerationError);
                }

                RunningState running = ReadRunningState(handle, out string? runningError);
                if (running == RunningState.Exited)
                {
                    return new LabProcessCloseResult(LabProcessCloseStatus.AlreadyExited);
                }

                if (running == RunningState.Unreadable)
                {
                    return new LabProcessCloseResult(
                        LabProcessCloseStatus.Unreadable,
                        runningError);
                }

                if (windows.Count > 1)
                {
                    return new LabProcessCloseResult(
                        LabProcessCloseStatus.MultipleCloseableWindows,
                        $"PID {expected.ProcessId} has {windows.Count} visible unowned windows; WM_CLOSE was not sent.");
                }

                if (windows.Count == 1)
                {
                    window = windows[0];
                    break;
                }

                uint remaining = GetRemainingTimeoutMilliseconds(
                    timeoutMilliseconds,
                    operationTimer.Elapsed);
                if (remaining == 0)
                {
                    return new LabProcessCloseResult(
                        LabProcessCloseStatus.NoCloseableWindow,
                        $"PID {expected.ProcessId} did not expose a visible unowned window within the clean-stop timeout.");
                }

                Thread.Sleep(checked((int)Math.Min(
                    WindowPollIntervalMilliseconds,
                    remaining)));
            }

            if (!IsExactCloseableWindow(window, expected.ProcessId))
            {
                return new LabProcessCloseResult(
                    LabProcessCloseStatus.CloseRequestFailed,
                    "The selected process window changed before WM_CLOSE; no close message was sent.");
            }

            if (!PostMessage(window, WindowMessageClose, IntPtr.Zero, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                return new LabProcessCloseResult(
                    LabProcessCloseStatus.CloseRequestFailed,
                    DescribeWindowsError("Windows could not post WM_CLOSE", error));
            }

            uint wait = WaitForSingleObject(
                handle,
                GetRemainingTimeoutMilliseconds(
                    timeoutMilliseconds,
                    operationTimer.Elapsed));
            return wait switch
            {
                WaitObject0 => new LabProcessCloseResult(LabProcessCloseStatus.Closed),
                WaitTimeout => new LabProcessCloseResult(LabProcessCloseStatus.TimedOut),
                WaitFailed => new LabProcessCloseResult(
                    LabProcessCloseStatus.Unreadable,
                    DescribeWindowsError(
                        "Windows could not wait for the exact owned process",
                        Marshal.GetLastWin32Error())),
                _ => new LabProcessCloseResult(
                    LabProcessCloseStatus.Unreadable,
                    $"Windows returned unexpected process wait result {wait}."),
            };
        }
    }

    internal static ProcessStartInfo CreateStartInfo(LabProcessStartSpec specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        string executablePath = NormalizeFullyQualifiedPath(
            specification.ExecutablePath,
            nameof(specification.ExecutablePath));
        string workingDirectory = NormalizeFullyQualifiedPath(
            specification.WorkingDirectory,
            nameof(specification.WorkingDirectory));
        ArgumentNullException.ThrowIfNull(specification.Arguments);
        ArgumentNullException.ThrowIfNull(specification.Environment);

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        foreach (string? argument in specification.Arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException(
                    "Process arguments cannot contain null values.",
                    nameof(specification));
            }

            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string? key, string? value) in specification.Environment)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('=') || key.Contains('\0'))
            {
                throw new ArgumentException(
                    "Process environment keys must be non-empty and cannot contain '=' or null characters.",
                    nameof(specification));
            }

            if (value is null || value.Contains('\0'))
            {
                throw new ArgumentException(
                    $"Process environment value '{key}' cannot be null or contain a null character.",
                    nameof(specification));
            }

            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private static LabProcessStartResult AbortUnverifiedStart(
        SafeProcessHandle exactCreatedProcess,
        int processId,
        string verificationError)
    {
        bool terminationRequested = TerminateProcess(
            exactCreatedProcess,
            FailedStartAbortExitCode);
        int terminationError = terminationRequested ? 0 : Marshal.GetLastWin32Error();
        uint wait = WaitForSingleObject(
            exactCreatedProcess,
            FailedStartAbortWaitMilliseconds);
        if (wait == WaitObject0)
        {
            return new LabProcessStartResult(
                LabProcessStartStatus.Unreadable,
                Error: $"{verificationError} The exact unverified child PID {processId} was aborted on its original CreateProcess handle before start returned.");
        }

        string abortError = terminationRequested
            ? $"Windows returned process wait result {wait} after the exact abort request."
            : DescribeWindowsError(
                "Windows could not abort the exact unverified child",
                terminationError);
        return new LabProcessStartResult(
            LabProcessStartStatus.Unreadable,
            Error: $"{verificationError} {abortError} PID {processId} may still be running, but no foreign process was opened or signaled.");
    }

    private static Verification VerifyExpectedIdentity(
        SafeProcessHandle handle,
        OwnedProcessIdentity expected,
        out string? error)
    {
        error = null;
        RunningState running = ReadRunningState(handle, out string? runningError);
        if (running == RunningState.Exited)
        {
            return Verification.Exited;
        }

        if (running == RunningState.Unreadable)
        {
            error = runningError;
            return Verification.Unreadable;
        }

        if (!TryReadIdentity(
                handle,
                expected.ProcessId,
                out OwnedProcessIdentity actual,
                out string? identityError))
        {
            error = identityError;
            return Verification.Unreadable;
        }

        if (actual.StartTimeUtc.UtcTicks != expected.StartTimeUtc.UtcTicks ||
            !ExecutableIdentity.AreEquivalent(actual.ExecutablePath, expected.ExecutablePath))
        {
            return Verification.IdentityMismatch;
        }

        running = ReadRunningState(handle, out runningError);
        if (running == RunningState.Exited)
        {
            return Verification.Exited;
        }

        if (running == RunningState.Unreadable)
        {
            error = runningError;
            return Verification.Unreadable;
        }

        return Verification.Running;
    }

    private static bool TryReadIdentity(
        SafeProcessHandle handle,
        int processId,
        out OwnedProcessIdentity identity,
        out string? error)
    {
        identity = null!;
        if (!GetProcessTimes(
                handle,
                out FileTime creationTime,
                out _,
                out _,
                out _))
        {
            int windowsError = Marshal.GetLastWin32Error();
            error = DescribeWindowsError("Windows could not read the process start time", windowsError);
            return false;
        }

        char[] path = new char[32768];
        uint pathLength = checked((uint)path.Length);
        if (!QueryFullProcessImageName(handle, flags: 0, path, ref pathLength) || pathLength == 0)
        {
            int windowsError = Marshal.GetLastWin32Error();
            error = DescribeWindowsError("Windows could not read the process executable path", windowsError);
            return false;
        }

        try
        {
            long fileTime = ((long)creationTime.HighDateTime << 32) | creationTime.LowDateTime;
            DateTimeOffset startTimeUtc = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
            string executablePath = Path.GetFullPath(
                new string(path, 0, checked((int)pathLength)));
            identity = new OwnedProcessIdentity(processId, startTimeUtc, executablePath);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Windows returned an invalid process identity: {exception.Message}";
            return false;
        }
    }

    private static RunningState ReadRunningState(
        SafeProcessHandle handle,
        out string? error)
    {
        uint wait = WaitForSingleObject(handle, milliseconds: 0);
        if (wait == WaitTimeout)
        {
            error = null;
            return RunningState.Running;
        }

        if (wait == WaitObject0)
        {
            error = null;
            return RunningState.Exited;
        }

        error = wait == WaitFailed
            ? DescribeWindowsError(
                "Windows could not read the exact process state",
                Marshal.GetLastWin32Error())
            : $"Windows returned unexpected process wait result {wait}.";
        return RunningState.Unreadable;
    }

    private static bool TryFindCloseableWindows(
        int processId,
        out IReadOnlyList<IntPtr> windows,
        out string? error)
    {
        List<IntPtr> found = [];
        EnumWindowsCallback callback = (window, parameter) =>
        {
            if (IsExactCloseableWindow(window, processId))
            {
                found.Add(window);
            }

            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            int windowsError = Marshal.GetLastWin32Error();
            windows = [];
            error = DescribeWindowsError("Windows could not enumerate process windows", windowsError);
            GC.KeepAlive(callback);
            return false;
        }

        GC.KeepAlive(callback);
        windows = found;
        error = null;
        return true;
    }

    private static bool IsExactCloseableWindow(IntPtr window, int processId)
    {
        _ = GetWindowThreadProcessId(window, out uint windowProcessId);
        return windowProcessId == checked((uint)processId)
            && GetWindow(window, GetWindowOwner) == IntPtr.Zero
            && IsWindowVisible(window);
    }

    private static bool IsValid(
        OwnedProcessIdentity identity,
        out string? error)
    {
        if (identity.ProcessId <= 0)
        {
            error = "An owned process identity requires a positive PID.";
            return false;
        }

        if (identity.StartTimeUtc == default)
        {
            error = "An owned process identity requires an exact UTC start time.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(identity.ExecutablePath))
        {
            error = "An owned process identity requires an executable path.";
            return false;
        }

        error = null;
        return true;
    }

    private static uint GetBoundedTimeoutMilliseconds(TimeSpan timeout)
    {
        const double maximumMilliseconds = uint.MaxValue - 1d;
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > maximumMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The process close timeout must be finite, non-negative, and fit in a Windows bounded wait.");
        }

        return checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private static uint GetRemainingTimeoutMilliseconds(
        uint timeoutMilliseconds,
        TimeSpan elapsed)
    {
        double remaining = timeoutMilliseconds - elapsed.TotalMilliseconds;
        return remaining <= 0
            ? 0
            : checked((uint)Math.Ceiling(remaining));
    }

    private static string NormalizeFullyQualifiedPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be fully qualified.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static bool IsMissingProcessError(int error) =>
        error is ErrorInvalidParameter or ErrorNotFound;

    private static string DescribeWindowsError(string action, int error) =>
        $"{action}: {new Win32Exception(error).Message} (Win32 {error}).";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Exact live-lab process ownership is implemented for Windows only.");
        }
    }

    private enum RunningState
    {
        Running,
        Exited,
        Unreadable,
    }

    private enum Verification
    {
        Running,
        Exited,
        IdentityMismatch,
        Unreadable,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        public readonly uint LowDateTime;
        public readonly uint HighDateTime;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        SafeProcessHandle process,
        uint exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetWindow(
        IntPtr window,
        uint command);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr window,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter);
}

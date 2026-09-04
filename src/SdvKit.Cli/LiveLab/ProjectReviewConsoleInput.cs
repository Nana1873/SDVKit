using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.Cli.LiveLab;

internal static class ProjectReviewConsoleLine
{
    public const int MaximumLength = 4096;

    public static bool CanRunBeforeScenarioReady(string line)
    {
        if (ValidationError(line) is not null)
        {
            return false;
        }

        string[] tokens = line.Split(' ');
        if (tokens.Any(string.IsNullOrEmpty)
            || !string.Equals(tokens[0], "sdvkit", StringComparison.Ordinal))
        {
            return false;
        }

        return IsInputCommand(tokens) || IsViewportScreenshotCommand(tokens);
    }

    public static string? ValidationError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "A project-review console command must contain one non-empty line.";
        }

        if (line.Length > MaximumLength)
        {
            return $"A project-review console command cannot exceed {MaximumLength} UTF-16 code units.";
        }

        for (var index = 0; index < line.Length; index++)
        {
            if (char.IsHighSurrogate(line[index]))
            {
                if (index + 1 >= line.Length || !char.IsLowSurrogate(line[index + 1]))
                {
                    return "A project-review console command must contain well-formed UTF-16 text.";
                }

                index++;
            }
            else if (char.IsLowSurrogate(line[index]))
            {
                return "A project-review console command must contain well-formed UTF-16 text.";
            }
        }

        if (line.Any(char.IsControl))
        {
            return "A project-review console command cannot contain control characters.";
        }

        return null;
    }

    private static bool IsInputCommand(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 4
            || !string.Equals(tokens[1], "input", StringComparison.Ordinal))
        {
            return false;
        }

        var actionIndex = 2;
        var transportRequest = false;
        if (tokens.Count >= 6
            && string.Equals(tokens[2], "request", StringComparison.Ordinal)
            && Guid.TryParseExact(tokens[3], "N", out _))
        {
            actionIndex = 4;
            transportRequest = true;
        }

        if (tokens.Count == actionIndex + 2
            && string.Equals(tokens[actionIndex], "press", StringComparison.Ordinal))
        {
            return IsAsciiAlphaNumeric(tokens[actionIndex + 1], maximumLength: 64)
                && (!transportRequest
                    || tokens[actionIndex + 1] is not (
                        "MouseWheelUp" or "MouseWheelDown"));
        }

        if (transportRequest
            && tokens.Count == actionIndex + 2
            && string.Equals(tokens[actionIndex], "wheel", StringComparison.Ordinal))
        {
            return tokens[actionIndex + 1] is "up" or "down";
        }

        if (!string.Equals(tokens[actionIndex], "cursor", StringComparison.Ordinal))
        {
            return false;
        }

        return tokens.Count == actionIndex + 2
                && string.Equals(tokens[actionIndex + 1], "clear", StringComparison.Ordinal)
            || tokens.Count == actionIndex + 3
                && TryParseUnsignedCoordinate(tokens[actionIndex + 1])
                && TryParseUnsignedCoordinate(tokens[actionIndex + 2]);
    }

    private static bool IsViewportScreenshotCommand(IReadOnlyList<string> tokens) =>
        tokens.Count == 4
            && string.Equals(tokens[1], "screenshot", StringComparison.Ordinal)
            && string.Equals(tokens[2], "viewport", StringComparison.Ordinal)
            && IsScreenshotLabel(tokens[3])
        || tokens.Count == 6
            && string.Equals(tokens[1], "screenshot", StringComparison.Ordinal)
            && string.Equals(tokens[2], "capture", StringComparison.Ordinal)
            && ReviewTransportToken.IsRequestId(tokens[3])
            && string.Equals(
                tokens[4],
                ReviewScreenshotContract.ViewportMode,
                StringComparison.Ordinal)
            && IsScreenshotLabel(tokens[5]);

    private static bool TryParseUnsignedCoordinate(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _);

    private static bool IsScreenshotLabel(string value) =>
        value.Length is >= 1 and <= 64
        && value.All(character =>
            IsAsciiAlphaNumeric(character)
            || character is '-' or '_');

    private static bool IsAsciiAlphaNumeric(string value, int maximumLength) =>
        value.Length >= 1
        && value.Length <= maximumLength
        && value.All(IsAsciiAlphaNumeric);

    private static bool IsAsciiAlphaNumeric(char character) =>
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9');
}

internal enum ProjectReviewConsoleInputStatus
{
    Written = 0,
    InvalidRequest = 10,
    ProcessExited = 11,
    ProcessIdentityMismatch = 12,
    ProcessUnreadable = 13,
    AttachFailed = 14,
    SharedConsole = 15,
    InputBusy = 16,
    InputOpenFailed = 17,
    WriteFailed = 18,
    PartialWrite = 19,
    WrittenDetachFailed = 20,
    WrittenProcessExited = 21,
    WrittenProcessUnreadable = 22,
    WrittenConsoleChanged = 23,
    WorkerTimedOut = 30,
    WorkerFailed = 31,
    WorkerStartFailed = 32,
    WorkerParentMismatch = 33,
}

internal sealed record ProjectReviewConsoleInputResult(
    ProjectReviewConsoleInputStatus Status,
    string? Error = null)
{
    public bool? CommandWritten => Status switch
    {
        ProjectReviewConsoleInputStatus.Written
            or ProjectReviewConsoleInputStatus.WrittenDetachFailed
            or ProjectReviewConsoleInputStatus.WrittenProcessExited
            or ProjectReviewConsoleInputStatus.WrittenProcessUnreadable
            or ProjectReviewConsoleInputStatus.WrittenConsoleChanged => true,
        ProjectReviewConsoleInputStatus.PartialWrite
            or ProjectReviewConsoleInputStatus.WorkerTimedOut
            or ProjectReviewConsoleInputStatus.WorkerFailed => null,
        _ => false,
    };
}

internal interface IProjectReviewConsoleInputSender
{
    ProjectReviewConsoleInputResult SendLine(
        OwnedProcessIdentity expected,
        string line);
}

internal sealed class WindowsProjectReviewConsoleInputSender
    : IProjectReviewConsoleInputSender
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ProjectReviewConsoleInputResult SendLine(
        OwnedProcessIdentity expected,
        string line)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string? validationError = ProjectReviewConsoleLine.ValidationError(line);
        if (validationError is not null)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.InvalidRequest,
                validationError);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerStartFailed,
                "Project-review console input is implemented for Windows only.");
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateWorkerStartInfo(expected);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException
                or Win32Exception)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerStartFailed,
                exception.Message);
        }

        Process? worker;
        try
        {
            worker = Process.Start(startInfo);
            if (worker is null)
            {
                return new ProjectReviewConsoleInputResult(
                    ProjectReviewConsoleInputStatus.WorkerStartFailed,
                    "Windows did not return the project-review console worker process.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or Win32Exception
                or UnauthorizedAccessException)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerStartFailed,
                exception.Message);
        }

        using (worker)
        {
            try
            {
                worker.StandardInput.Write(line);
                worker.StandardInput.Close();
                if (!worker.WaitForExit(checked((int)WorkerTimeout.TotalMilliseconds)))
                {
                    try
                    {
                        worker.Kill(entireProcessTree: true);
                        worker.WaitForExit();
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException
                            or NotSupportedException
                            or Win32Exception)
                    {
                        return new ProjectReviewConsoleInputResult(
                            ProjectReviewConsoleInputStatus.WorkerTimedOut,
                            $"The console worker timed out and its exact termination could not be confirmed: {exception.Message}");
                    }

                    return new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.WorkerTimedOut,
                        "The console worker timed out; command delivery is unknown and must not be retried automatically.");
                }

                string error = worker.StandardError.ReadToEnd().Trim();
                ProjectReviewConsoleInputStatus status = Enum.IsDefined(
                    typeof(ProjectReviewConsoleInputStatus),
                    worker.ExitCode)
                        ? (ProjectReviewConsoleInputStatus)worker.ExitCode
                        : ProjectReviewConsoleInputStatus.WorkerFailed;
                return new ProjectReviewConsoleInputResult(
                    status,
                    string.IsNullOrWhiteSpace(error) ? null : error);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or Win32Exception
                    or UnauthorizedAccessException)
            {
                return new ProjectReviewConsoleInputResult(
                    ProjectReviewConsoleInputStatus.WorkerFailed,
                    exception.Message);
            }
        }
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(
        OwnedProcessIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current SDVKit executable path is unavailable.");
        string processName = Path.GetFileNameWithoutExtension(processPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = StrictUtf8,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8,
        };

        if (string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assemblyPath = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The current SDVKit assembly path is unavailable.");
            startInfo.ArgumentList.Add(assemblyPath);
        }
        else if (!string.Equals(processName, "sdvkit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The current process '{processName}' is not the SDVKit app host.");
        }

        startInfo.ArgumentList.Add(WindowsProjectReviewConsoleInputWorker.Argument);
        startInfo.ArgumentList.Add(expected.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(expected.StartTimeUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(expected.ExecutablePath);
        using Process parent = Process.GetCurrentProcess();
        startInfo.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }
}

internal static class WindowsProjectReviewConsoleInputWorker
{
    internal const string Argument = "--internal-project-review-console-input";

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint ToolhelpSnapshotProcess = 0x00000002;
    private const uint EnableLineInput = 0x0002;
    private const ushort KeyEvent = 0x0001;
    private const ushort VirtualKeyReturn = 0x000D;
    private const ushort VirtualKeyEscape = 0x001B;

    internal static bool IsInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 0
        && string.Equals(arguments[0], Argument, StringComparison.Ordinal);

    internal static int Run(
        IReadOnlyList<string> arguments,
        TextReader input,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(error);

        ProjectReviewConsoleInputResult result = new(
            ProjectReviewConsoleInputStatus.WorkerFailed,
            "The console worker failed before it could determine command delivery.");
        if (arguments.Count != 6
            || !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0
            || !long.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long startTicks)
            || startTicks <= 0
            || string.IsNullOrWhiteSpace(arguments[3])
            || !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parentProcessId)
            || parentProcessId <= 0
            || !long.TryParse(
                arguments[5],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parentStartTicks)
            || parentStartTicks <= 0)
        {
            result = new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.InvalidRequest,
                "The internal console worker request is invalid.");
        }
        else
        {
            using SafeProcessHandle? parentHandle = OpenVerifiedParent(
                parentProcessId,
                parentStartTicks,
                out ProjectReviewConsoleInputResult? parentFailure);
            if (parentHandle is null)
            {
                result = parentFailure!;
            }
            else
            {
                try
                {
                    string line = input.ReadToEnd();
                    var expected = new OwnedProcessIdentity(
                        processId,
                        new DateTimeOffset(startTicks, TimeSpan.Zero),
                        arguments[3]);
                    result = WriteLine(expected, line);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or IOException
                        or NotSupportedException
                        or PathTooLongException
                        or UnauthorizedAccessException)
                {
                    result = new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.InvalidRequest,
                        exception.Message);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            error.WriteLine(result.Error);
        }

        return (int)result.Status;
    }

    internal static ProjectReviewConsoleInputResult WriteLine(
        OwnedProcessIdentity expected,
        string line)
    {
        string? validationError = ProjectReviewConsoleLine.ValidationError(line);
        if (validationError is not null)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.InvalidRequest,
                validationError);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerFailed,
                "Project-review console input is implemented for Windows only.");
        }

        // A console-subsystem app host can inherit the caller's console even when
        // the one-shot worker is hidden and all standard streams are redirected.
        // Detach only this dedicated worker before attaching to the owned SMAPI
        // console; the main SDVKit process must retain its original console.
        if (GetConsoleCP() != 0 && !FreeConsole())
        {
            return Failure(
                ProjectReviewConsoleInputStatus.AttachFailed,
                "The console worker could not detach from its inherited console");
        }

        using SafeProcessHandle? processHandle = WindowsLabProcessHost.OpenVerifiedProcess(
            expected,
            out LabProcessInspectResult inspection);
        if (processHandle is null)
        {
            return InspectionFailure(inspection);
        }

        if (!AttachConsole(checked((uint)expected.ProcessId)))
        {
            return Failure(
                ProjectReviewConsoleInputStatus.AttachFailed,
                "Windows could not attach to the exact owned SMAPI console");
        }

        ProjectReviewConsoleInputResult result = new(
            ProjectReviewConsoleInputStatus.WorkerFailed,
            "The console worker failed before it could determine command delivery.");
        try
        {
            result = WriteAttachedConsole(processHandle, expected.ProcessId, line);
        }
        finally
        {
            if (!FreeConsole())
            {
                int windowsError = Marshal.GetLastWin32Error();
                result = result.Status == ProjectReviewConsoleInputStatus.Written
                    ? new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.WrittenDetachFailed,
                        DescribeWindowsError(
                            "The command was fully enqueued, but the console worker could not detach",
                            windowsError))
                    : result with
                    {
                        Error = $"{result.Error} {DescribeWindowsError("The console worker could not detach", windowsError)}".Trim(),
                    };
            }
        }

        return result;
    }

    internal static ConsoleInputRecord[] CreateInputRecords(string line)
    {
        string? validationError = ProjectReviewConsoleLine.ValidationError(line);
        if (validationError is not null)
        {
            throw new ArgumentException(validationError, nameof(line));
        }

        // ReadConsole can retain an unsubmitted edited line after its input event
        // queue is empty. Clear that cooked line without executing it before
        // enqueueing the exact SDVKit command.
        var records = new ConsoleInputRecord[checked((line.Length + 2) * 2)];
        var index = 0;
        records[index++] = Record('\u001b', keyDown: true, VirtualKeyEscape);
        records[index++] = Record('\u001b', keyDown: false, VirtualKeyEscape);
        foreach (char character in line)
        {
            records[index++] = Record(character, keyDown: true, virtualKeyCode: 0);
            records[index++] = Record(character, keyDown: false, virtualKeyCode: 0);
        }

        records[index++] = Record('\r', keyDown: true, VirtualKeyReturn);
        records[index] = Record('\r', keyDown: false, VirtualKeyReturn);
        return records;
    }

    internal static bool UsesCookedLineInput(uint consoleMode) =>
        (consoleMode & EnableLineInput) != 0;

    private static ProjectReviewConsoleInputResult WriteAttachedConsole(
        SafeProcessHandle processHandle,
        int expectedProcessId,
        string line)
    {
        if (!TryReadConsoleProcesses(out uint[] consoleProcesses, out string? processError))
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.AttachFailed,
                processError);
        }

        uint currentProcessId = checked((uint)Environment.ProcessId);
        uint targetProcessId = checked((uint)expectedProcessId);
        if (consoleProcesses.Length != 2
            || !consoleProcesses.Contains(currentProcessId)
            || !consoleProcesses.Contains(targetProcessId))
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.SharedConsole,
                "The attached console is not exclusively owned by the exact SMAPI process and the one-shot SDVKit worker; no input was written.");
        }

        LabProcessInspectResult running =
            WindowsLabProcessHost.InspectVerifiedProcessHandle(processHandle);
        if (running.Status != LabProcessInspectStatus.Running)
        {
            return InspectionFailure(running);
        }

        using SafeFileHandle inputHandle = CreateFile(
            "CONIN$",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            flagsAndAttributes: 0,
            IntPtr.Zero);
        if (inputHandle.IsInvalid)
        {
            return Failure(
                ProjectReviewConsoleInputStatus.InputOpenFailed,
                "Windows could not open the exact SMAPI console input buffer");
        }

        if (!GetConsoleMode(inputHandle, out uint consoleMode))
        {
            return Failure(
                ProjectReviewConsoleInputStatus.InputOpenFailed,
                "Windows could not inspect the exact SMAPI console input mode");
        }

        if (!UsesCookedLineInput(consoleMode))
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.InputOpenFailed,
                "The exact SMAPI console is not using the required cooked line-input mode; no command was written.");
        }

        if (!GetNumberOfConsoleInputEvents(inputHandle, out uint pendingEvents))
        {
            return Failure(
                ProjectReviewConsoleInputStatus.InputOpenFailed,
                "Windows could not inspect the exact SMAPI console input buffer");
        }

        if (pendingEvents != 0)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.InputBusy,
                $"The exact SMAPI console has {pendingEvents} pending input event(s); no command was written.");
        }

        ConsoleInputRecord[] records = CreateInputRecords(line);
        if (!WriteConsoleInput(
                inputHandle,
                records,
                checked((uint)records.Length),
                out uint written))
        {
            int windowsError = Marshal.GetLastWin32Error();
            return new ProjectReviewConsoleInputResult(
                written == 0
                    ? ProjectReviewConsoleInputStatus.WriteFailed
                    : ProjectReviewConsoleInputStatus.PartialWrite,
                DescribeWindowsError(
                    written == 0
                        ? "Windows could not write to the exact SMAPI console input buffer"
                        : $"Windows wrote only {written} of {records.Length} input records; delivery is unknown and must not be retried automatically",
                    windowsError));
        }

        if (written != records.Length)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.PartialWrite,
                $"Windows wrote only {written} of {records.Length} input records; delivery is unknown and must not be retried automatically.");
        }

        LabProcessInspectResult afterWrite =
            WindowsLabProcessHost.InspectVerifiedProcessHandle(processHandle);
        if (afterWrite.Status == LabProcessInspectStatus.Exited)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WrittenProcessExited,
                "The complete command was enqueued, but the exact SMAPI process exited before delivery could be rechecked; do not retry it automatically.");
        }

        if (afterWrite.Status != LabProcessInspectStatus.Running)
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WrittenProcessUnreadable,
                $"The complete command was enqueued, but the exact SMAPI process became unreadable before delivery could be rechecked; do not retry it automatically. {afterWrite.Error}".Trim());
        }

        if (!TryReadConsoleProcesses(out uint[] afterWriteProcesses, out string? afterWriteError))
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WrittenProcessUnreadable,
                $"The complete command was enqueued, but the console ownership could not be rechecked; do not retry it automatically. {afterWriteError}".Trim());
        }

        if (afterWriteProcesses.Length != 2
            || !afterWriteProcesses.Contains(currentProcessId)
            || !afterWriteProcesses.Contains(targetProcessId))
        {
            return new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WrittenConsoleChanged,
                "The complete command was enqueued, but the console process set changed before delivery could be rechecked; do not retry it automatically.");
        }

        return new ProjectReviewConsoleInputResult(ProjectReviewConsoleInputStatus.Written);
    }

    private static SafeProcessHandle? OpenVerifiedParent(
        int expectedParentProcessId,
        long expectedParentStartTicks,
        out ProjectReviewConsoleInputResult? failure)
    {
        if (!TryGetParentProcessId(Environment.ProcessId, out int actualParentProcessId)
            || actualParentProcessId != expectedParentProcessId)
        {
            failure = new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerParentMismatch,
                "The internal console worker was not started by the exact SDVKit parent process; no console input was written.");
            return null;
        }

        string? parentExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(parentExecutablePath))
        {
            failure = new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerParentMismatch,
                "The internal console worker could not resolve its SDVKit executable identity; no console input was written.");
            return null;
        }

        var expectedParent = new OwnedProcessIdentity(
            expectedParentProcessId,
            new DateTimeOffset(expectedParentStartTicks, TimeSpan.Zero),
            parentExecutablePath);
        SafeProcessHandle? parentHandle = WindowsLabProcessHost.OpenVerifiedProcess(
            expectedParent,
            out LabProcessInspectResult inspection);
        if (parentHandle is null)
        {
            failure = new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.WorkerParentMismatch,
                inspection.Error
                    ?? "The internal console worker could not verify its exact SDVKit parent process; no console input was written.");
            return null;
        }

        failure = null;
        return parentHandle;
    }

    private static bool TryGetParentProcessId(
        int processId,
        out int parentProcessId)
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(
            ToolhelpSnapshotProcess,
            processId: 0);
        if (snapshot.IsInvalid)
        {
            parentProcessId = 0;
            return false;
        }

        var entry = new ProcessEntry32
        {
            Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
        };
        if (!Process32First(snapshot, ref entry))
        {
            parentProcessId = 0;
            return false;
        }

        do
        {
            if (entry.ProcessId == processId)
            {
                parentProcessId = checked((int)entry.ParentProcessId);
                return parentProcessId > 0;
            }
        }
        while (Process32Next(snapshot, ref entry));

        parentProcessId = 0;
        return false;
    }

    private static ProjectReviewConsoleInputResult InspectionFailure(
        LabProcessInspectResult inspection) => inspection.Status switch
        {
            LabProcessInspectStatus.Exited => new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.ProcessExited,
                inspection.Error ?? "The exact owned SMAPI process exited before console input."),
            LabProcessInspectStatus.IdentityMismatch => new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.ProcessIdentityMismatch,
                inspection.Error ?? "The PID no longer identifies the exact owned SMAPI process."),
            _ => new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.ProcessUnreadable,
                inspection.Error ?? "The exact owned SMAPI process could not be verified."),
        };

    private static bool TryReadConsoleProcesses(
        out uint[] processes,
        out string? error)
    {
        uint[] buffer = new uint[4];
        uint count = GetConsoleProcessList(buffer, checked((uint)buffer.Length));
        if (count == 0)
        {
            processes = [];
            error = DescribeWindowsError(
                "Windows could not enumerate the attached console processes",
                Marshal.GetLastWin32Error());
            return false;
        }

        if (count > buffer.Length)
        {
            if (count > 64)
            {
                processes = [];
                error = $"The attached console reported an unexpected process count of {count}.";
                return false;
            }

            buffer = new uint[count];
            count = GetConsoleProcessList(buffer, checked((uint)buffer.Length));
            if (count == 0 || count > buffer.Length)
            {
                processes = [];
                error = count == 0
                    ? DescribeWindowsError(
                        "Windows could not enumerate the attached console processes",
                        Marshal.GetLastWin32Error())
                    : "The attached console process list changed while it was being verified.";
                return false;
            }
        }

        processes = buffer[..checked((int)count)];
        error = null;
        return true;
    }

    private static ConsoleInputRecord Record(
        char character,
        bool keyDown,
        ushort virtualKeyCode) => new()
        {
            EventType = KeyEvent,
            KeyEvent = new ConsoleKeyEventRecord
            {
                KeyDown = keyDown,
                RepeatCount = 1,
                VirtualKeyCode = virtualKeyCode,
                UnicodeChar = character,
            },
        };

    private static ProjectReviewConsoleInputResult Failure(
        ProjectReviewConsoleInputStatus status,
        string action) => new(
            status,
            DescribeWindowsError(action, Marshal.GetLastWin32Error()));

    private static string DescribeWindowsError(string action, int error) =>
        $"{action}: {new Win32Exception(error).Message} (Win32 {error}).";

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
    internal struct ConsoleInputRecord
    {
        [FieldOffset(0)]
        internal ushort EventType;

        [FieldOffset(4)]
        internal ConsoleKeyEventRecord KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ConsoleKeyEventRecord
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool KeyDown;

        internal ushort RepeatCount;
        internal ushort VirtualKeyCode;
        internal ushort VirtualScanCode;
        internal char UnicodeChar;
        internal uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint Size;
        internal uint Usage;
        internal int ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint Threads;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        SafeFileHandle snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        SafeFileHandle snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(
        SafeFileHandle consoleHandle,
        out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfConsoleInputEvents(
        SafeFileHandle consoleInput,
        out uint numberOfEvents);

    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInput(
        SafeFileHandle consoleInput,
        [In] ConsoleInputRecord[] buffer,
        uint length,
        out uint numberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processList,
        uint processCount);
}

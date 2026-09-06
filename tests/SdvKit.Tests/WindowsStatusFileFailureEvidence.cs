using System.Runtime.InteropServices;
using SdvKit.Tests;

namespace SdvKit.AlwaysOn;

internal static partial class WindowsStatusFile
{
    [ThreadStatic]
    private static string? capturedStatusPath;

    public static void CaptureTestFailures(string statusPath, Action writes)
    {
        string? previousPath = capturedStatusPath;
        capturedStatusPath = statusPath;
        try
        {
            writes();
        }
        finally
        {
            capturedStatusPath = previousPath;
        }
    }

    static partial void RecordFailure(int error, string temporaryPath, string statusPath)
    {
        try
        {
            uint nativeStatus = RtlGetLastNtStatus();
            if (error == 5 && string.Equals(statusPath, capturedStatusPath, StringComparison.Ordinal))
            {
                uint mappedError = RtlNtStatusToDosError(nativeStatus);
                StatusConcurrencyFailure.CaptureNativeFailure(temporaryPath, statusPath, new
                {
                    StatusPath = statusPath,
                    TemporaryPath = temporaryPath,
                    Win32Error = error,
                    LastNativeStatusObservation = $"0x{nativeStatus:X8}",
                    ObservedStatusMappedToWin32 = mappedError,
                    ObservationMatchesWin32Error = mappedError == error,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    ProcessId = Environment.ProcessId,
                    ManagedThreadId = Environment.CurrentManagedThreadId,
                    NativeThreadId = GetCurrentThreadId(),
                });
            }
        }
        catch (Exception)
        {
            // The original publication error must survive failed test diagnostics.
        }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RtlGetLastNtStatus();

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RtlNtStatusToDosError(uint status);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();
}

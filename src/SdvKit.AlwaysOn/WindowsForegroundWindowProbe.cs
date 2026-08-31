using System.Runtime.InteropServices;

namespace SdvKit.AlwaysOn;

internal readonly record struct WindowsForegroundWindowObservation(
    long? WindowHandle,
    int? ProcessId,
    bool? IsCurrentProcess)
{
    public bool IsVerifiedUnfocused =>
        WindowHandle is not (null or 0)
        && ProcessId is > 0
        && IsCurrentProcess == false;
}

internal static class WindowsForegroundWindowProbe
{
    public static WindowsForegroundWindowObservation Observe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return default;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return FromNativeObservation(window, processId, Environment.ProcessId);
    }

    internal static WindowsForegroundWindowObservation FromNativeObservation(
        IntPtr window,
        uint processId,
        int currentProcessId)
    {
        if (window == IntPtr.Zero
            || processId == 0
            || processId > int.MaxValue
            || currentProcessId <= 0)
        {
            return default;
        }

        int observedProcessId = (int)processId;
        return new WindowsForegroundWindowObservation(
            window.ToInt64(),
            observedProcessId,
            observedProcessId == currentProcessId);
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}

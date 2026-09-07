using System.Runtime.InteropServices;

namespace SdvKit.AlwaysOn;

internal static class WindowsReviewConsole
{
    public static bool ShowWithoutActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            return false;
        }

        // The launcher creates this console minimized to avoid terminal-host activation.
        // Restore it once SMAPI is ready, without attaching to or activating another window.
        _ = ShowWindow(window, 4); // SW_SHOWNOACTIVATE
        return IsWindowVisible(window) && !IsIconic(window);
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}

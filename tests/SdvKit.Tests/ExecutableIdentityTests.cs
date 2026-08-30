using System.ComponentModel;
using System.Runtime.InteropServices;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ExecutableIdentityTests
{
    [Fact]
    public void WindowsHardlinkAliasMatchesButIndependentCopyDoesNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string executable = temporary.WriteFile(
            "game/StardewModdingAPI.exe",
            "same executable bytes");
        string hardlink = Path.Combine(
            temporary.Path,
            "game",
            "StardewModdingAPI-hardlink.exe");
        string independentCopy = temporary.WriteFile(
            "game/StardewModdingAPI-copy.exe",
            "same executable bytes");
        CreateHardlink(hardlink, executable);

        Assert.True(ExecutableIdentity.AreEquivalent(executable, hardlink));
        Assert.False(ExecutableIdentity.AreEquivalent(executable, independentCopy));
        Assert.False(ExecutableIdentity.AreEquivalent(
            Path.Combine(temporary.Path, "game", "missing-a.exe"),
            Path.Combine(temporary.Path, "game", "missing-b.exe")));
    }

    private static void CreateHardlink(string linkPath, string existingPath)
    {
        if (!CreateHardLink(linkPath, existingPath, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not create the hardlink test fixture.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}

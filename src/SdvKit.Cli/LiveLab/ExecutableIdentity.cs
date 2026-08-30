using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.Cli.LiveLab;

internal static class ExecutableIdentity
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public static bool AreEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        string normalizedLeft;
        string normalizedRight;
        try
        {
            normalizedLeft = Path.GetFullPath(left);
            normalizedRight = Path.GetFullPath(right);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using SafeFileHandle leftHandle = OpenWindowsIdentityHandle(normalizedLeft);
        if (leftHandle.IsInvalid)
        {
            return false;
        }

        using SafeFileHandle rightHandle = OpenWindowsIdentityHandle(normalizedRight);
        return !rightHandle.IsInvalid
            && TryReadWindowsFileIdentity(leftHandle, out WindowsFileIdentity leftIdentity)
            && TryReadWindowsFileIdentity(rightHandle, out WindowsFileIdentity rightIdentity)
            && leftIdentity == rightIdentity;
    }

    private static SafeFileHandle OpenWindowsIdentityHandle(string path) =>
        CreateFile(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

    private static bool TryReadWindowsFileIdentity(
        SafeFileHandle handle,
        out WindowsFileIdentity identity)
    {
        identity = default;
        if (!GetFileInformationByHandleEx(
                handle,
                FileInformationClass.FileIdInfo,
                out FileIdInfo information,
                checked((uint)Marshal.SizeOf<FileIdInfo>())))
        {
            return false;
        }

        identity = new WindowsFileIdentity(
            information.VolumeSerialNumber,
            information.FileId.Low,
            information.FileId.High);
        return true;
    }

    private readonly record struct WindowsFileIdentity(
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    private enum FileInformationClass
    {
        FileIdInfo = 18,
    }

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
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);
}

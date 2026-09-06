using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.AlwaysOn;

internal static partial class WindowsStatusFile
{
    private const uint DeleteAccess = 0x00010000;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x00000001;
    private const uint PosixSemantics = 0x00000002;

    public static void Publish(string temporaryPath, string statusPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new IOException("Atomic lab status publication requires Windows 10 version 1709 or later.");
        }

        // ReplaceFile creates another temporary destination which can itself be
        // held open by a reader (1175). Rename the completed snapshot directly;
        // POSIX semantics preserve open old snapshots while new opens see this one.
        using SafeFileHandle handle = CreateFile(
            ExtendedPath(temporaryPath),
            DeleteAccess,
            FileShare.Read | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw PublicationError("open the completed status snapshot", Marshal.GetLastWin32Error());
        }

        byte[] name = Encoding.Unicode.GetBytes(ExtendedPath(statusPath));
        var information = new FileRenameInformation
        {
            Flags = ReplaceIfExists | PosixSemantics,
            RootDirectory = IntPtr.Zero,
            FileNameLength = checked((uint)name.Length),
            FileName = '\0',
        };
        int size = checked(Marshal.SizeOf<FileRenameInformation>() + name.Length);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            int nameOffset = Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileName)).ToInt32();
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, checked((uint)size)))
            {
                int error = Marshal.GetLastWin32Error();
                RecordFailure(error, temporaryPath, statusPath);
                throw PublicationError("rename the completed status snapshot", error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static partial void RecordFailure(int error, string temporaryPath, string statusPath);

    private static string ExtendedPath(string path)
    {
        string absolute = Path.GetFullPath(path);
        return absolute.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? absolute
            : absolute.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + absolute[2..]
                : @"\\?\" + absolute;
    }

    private static IOException PublicationError(string operation, int error) =>
        new($"Windows could not {operation} (error {error}): {new Win32Exception(error).Message}",
            unchecked((int)(0x80070000u | (uint)error)));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInformation
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle, int informationClass, IntPtr information, uint bufferSize);
}

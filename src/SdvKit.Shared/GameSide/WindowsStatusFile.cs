using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.GameSide;

internal static class WindowsStatusFile
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const int FileBasicInfo = 0;
    private const int FileRenameInfoEx = 22;
    private const int FileDispositionInfoEx = 21;
    private const uint ReplaceIfExists = 0x00000001;
    private const uint PosixSemantics = 0x00000002;
    private const uint DispositionDelete = 0x00000001;
    private const uint DispositionPosixSemantics = 0x00000002;
    private const int ErrorAccessDenied = 5;
    private const uint ReadOnlyAttribute = 0x00000001;

    /// <summary>Suffix of the name the previous snapshot briefly carries while a held destination is replaced.</summary>
    internal const string ReplacedSuffix = ".replaced";

    private static long heldDestinationReplacements;

    /// <summary>How many publications replaced a destination that Windows refused to replace in place; see <see cref="Publish"/>.</summary>
    internal static long HeldDestinationReplacements => Interlocked.Read(ref heldDestinationReplacements);

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

        using RenameBuffer destination = new(statusPath, ReplaceIfExists | PosixSemantics);
        int error;
        if (SimulatedHeldDestinationDenials > 0)
        {
            SimulatedHeldDestinationDenials--;
            error = ErrorAccessDenied;
        }
        else if (SetFileInformationByHandle(handle, FileRenameInfoEx, destination.Pointer, destination.Size))
        {
            return;
        }
        else
        {
            error = Marshal.GetLastWin32Error();
        }

        if (error == ErrorAccessDenied && TryReplaceHeldDestination(handle, destination, statusPath))
        {
            return;
        }

        FailureObserver?.Invoke(error, temporaryPath, statusPath);
        throw PublicationError("rename the completed status snapshot", error);
    }

    /// <summary>
    /// Windows refuses to replace a destination that a kernel-mode component currently holds open
    /// (observed as the System process, for example a real-time file scan) with ERROR_ACCESS_DENIED,
    /// although the held file itself may still be renamed and deleted with POSIX semantics.
    /// Move the held snapshot aside, give the completed snapshot the destination name, and remove the
    /// aside name at once. Open readers keep the old snapshot; later opens see the new one. The
    /// destination name is absent for the moment between the two renames, which the reader tolerates.
    /// A read-only destination, an unopenable one, and every other error keep the original denial.
    /// </summary>
    private static bool TryReplaceHeldDestination(SafeFileHandle completed, RenameBuffer destination, string statusPath)
    {
        using SafeFileHandle held = CreateFile(
            ExtendedPath(statusPath),
            DeleteAccess | FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        if (held.IsInvalid || IsReadOnly(held))
        {
            return false;
        }

        using RenameBuffer aside = new(statusPath + ReplacedSuffix, ReplaceIfExists | PosixSemantics);
        if (!SetFileInformationByHandle(held, FileRenameInfoEx, aside.Pointer, aside.Size))
        {
            return false;
        }

        if (!SetFileInformationByHandle(completed, FileRenameInfoEx, destination.Pointer, destination.Size))
        {
            // Give the previous snapshot its name back so readers keep a complete snapshot.
            SetFileInformationByHandle(held, FileRenameInfoEx, destination.Pointer, destination.Size);
            return false;
        }

        Interlocked.Increment(ref heldDestinationReplacements);
        IntPtr disposition = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(disposition, unchecked((int)(DispositionDelete | DispositionPosixSemantics)));
            if (!SetFileInformationByHandle(held, FileDispositionInfoEx, disposition, sizeof(uint)))
            {
                // The publication itself succeeded. A leftover aside name is replaced by the next
                // held-destination replacement and ignored by the reader.
                AsideCleanupObserver?.Invoke(Marshal.GetLastWin32Error(), statusPath + ReplacedSuffix);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(disposition);
        }

        return true;
    }

    private static bool IsReadOnly(SafeFileHandle handle)
    {
        const int basicInfoSize = 40;
        IntPtr buffer = Marshal.AllocHGlobal(basicInfoSize);
        try
        {
            if (!GetFileInformationByHandleEx(handle, FileBasicInfo, buffer, basicInfoSize))
            {
                return true;
            }

            uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, 32));
            return (attributes & ReadOnlyAttribute) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Test-only diagnostics; production leaves these thread-local observers unset.
    [ThreadStatic]
    internal static Action<int, string, string>? FailureObserver;

    [ThreadStatic]
    internal static Action<int, string>? AsideCleanupObserver;

    /// <summary>
    /// Test-only: while positive, the next in-place replacements are treated as denied with
    /// ERROR_ACCESS_DENIED without being attempted, so the held-destination path can be exercised
    /// deterministically. Production leaves it at zero.
    /// </summary>
    [ThreadStatic]
    internal static int SimulatedHeldDestinationDenials;

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

    private sealed class RenameBuffer : IDisposable
    {
        public RenameBuffer(string path, uint flags)
        {
            byte[] name = Encoding.Unicode.GetBytes(ExtendedPath(path));
            var information = new FileRenameInformation
            {
                Flags = flags,
                RootDirectory = IntPtr.Zero,
                FileNameLength = checked((uint)name.Length),
                FileName = '\0',
            };
            int size = checked(Marshal.SizeOf<FileRenameInformation>() + name.Length);
            Pointer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(information, Pointer, fDeleteOld: false);
            int nameOffset = Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileName)).ToInt32();
            Marshal.Copy(name, 0, IntPtr.Add(Pointer, nameOffset), name.Length);
            Marshal.WriteInt16(Pointer, nameOffset + name.Length, 0);
            Size = checked((uint)size);
        }

        public IntPtr Pointer { get; }

        public uint Size { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, IntPtr information, uint bufferSize);
}

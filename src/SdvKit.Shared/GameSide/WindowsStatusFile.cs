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
        AsideCleanupError = null;
        UsedHeldDestinationFallback = false;
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

        IOException failure = PublicationError("rename the completed status snapshot", error);
        if (error == ErrorAccessDenied)
        {
            if (TryReplaceHeldDestination(handle, destination, statusPath, failure, out IOException? stepFailure))
            {
                return;
            }

            failure = stepFailure ?? failure;
        }

        FailureObserver?.Invoke(failure.HResult & 0xFFFF, temporaryPath, statusPath);
        throw failure;
    }

    /// <summary>
    /// Windows refuses to replace a destination that a kernel-mode component currently holds open
    /// (observed as the System process, for example a real-time file scan) with ERROR_ACCESS_DENIED,
    /// although the held file itself may still be renamed and deleted with POSIX semantics.
    /// Move the held snapshot aside, give the completed snapshot the destination name, and remove the
    /// aside name at once. Open readers keep the old snapshot; later opens see the new one. The
    /// destination name is absent for the moment between the two renames, which the reader tolerates.
    /// A read-only or unopenable destination keeps the original denial (<paramref name="stepFailure"/>
    /// stays null); a failing step after that reports its own error and where the previous snapshot is.
    /// </summary>
    private static bool TryReplaceHeldDestination(
        SafeFileHandle completed,
        RenameBuffer destination,
        string statusPath,
        IOException denial,
        out IOException? stepFailure)
    {
        stepFailure = null;
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

        string asidePath = statusPath + ReplacedSuffix;
        using RenameBuffer aside = new(asidePath, ReplaceIfExists | PosixSemantics);
        if (!SetFileInformationByHandle(held, FileRenameInfoEx, aside.Pointer, aside.Size))
        {
            int asideError = Marshal.GetLastWin32Error();
            stepFailure = new IOException(
                $"{denial.Message} Moving the held snapshot aside to '{asidePath}' failed (error {asideError}): "
                + $"{new Win32Exception(asideError).Message} The previous snapshot keeps its name.",
                denial.HResult);
            return false;
        }

        if (!SetFileInformationByHandle(completed, FileRenameInfoEx, destination.Pointer, destination.Size))
        {
            int renameError = Marshal.GetLastWin32Error();
            // Give the previous snapshot its name back so readers keep a complete snapshot.
            string outcome = SetFileInformationByHandle(held, FileRenameInfoEx, destination.Pointer, destination.Size)
                ? "The previous snapshot was given its name back."
                : $"Restoring the previous snapshot failed (error {Marshal.GetLastWin32Error()}); it remains at '{asidePath}'.";
            stepFailure = new IOException(
                $"{denial.Message} Renaming the completed snapshot to the destination after moving the held snapshot aside "
                + $"failed (error {renameError}): {new Win32Exception(renameError).Message} {outcome}",
                unchecked((int)(0x80070000u | (uint)renameError)));
            return false;
        }

        Interlocked.Increment(ref heldDestinationReplacements);
        UsedHeldDestinationFallback = true;
        IntPtr disposition = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(disposition, unchecked((int)(DispositionDelete | DispositionPosixSemantics)));
            int deleteError = 0;
            if (SimulatedAsideDeleteFailures > 0)
            {
                SimulatedAsideDeleteFailures--;
                deleteError = ErrorAccessDenied;
            }
            else if (!SetFileInformationByHandle(held, FileDispositionInfoEx, disposition, sizeof(uint)))
            {
                deleteError = Marshal.GetLastWin32Error();
            }

            if (deleteError != 0)
            {
                // The publication itself succeeded. A leftover aside name is replaced by the next
                // held-destination replacement and ignored by the reader, but the caller reports it.
                AsideCleanupError = $"Removing the aside name '{asidePath}' after a held-destination replacement failed "
                    + $"(error {deleteError}): {new Win32Exception(deleteError).Message} The new snapshot is in place; "
                    + "the leftover is replaced by the next fallback replacement.";
                AsideCleanupObserver?.Invoke(deleteError, asidePath);
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

    /// <summary>Whether the last <see cref="Publish"/> on this thread succeeded through the held-destination fallback.</summary>
    [ThreadStatic]
    internal static bool UsedHeldDestinationFallback;

    /// <summary>
    /// Set by the last <see cref="Publish"/> on this thread when it succeeded through the held-destination
    /// fallback but could not remove the aside name; null otherwise. The caller reports it.
    /// </summary>
    [ThreadStatic]
    internal static string? AsideCleanupError;

    // Test-only diagnostics; production leaves these thread-local observers unset.
    [ThreadStatic]
    internal static Action<int, string, string>? FailureObserver;

    [ThreadStatic]
    internal static Action<int, string>? AsideCleanupObserver;

    /// <summary>Test-only: while positive, the next aside-name removals fail with ERROR_ACCESS_DENIED without being attempted.</summary>
    [ThreadStatic]
    internal static int SimulatedAsideDeleteFailures;

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

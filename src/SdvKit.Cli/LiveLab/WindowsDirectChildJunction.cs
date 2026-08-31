using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.Cli.LiveLab;

internal interface IDirectChildJunction
{
    void VerifyInactive(string savesRoot, string slotName, string targetPath);

    string Activate(string savesRoot, string slotName, string targetPath);

    void VerifyActive(string savesRoot, string slotName, string targetPath);

    void EnsureInactive(string savesRoot, string slotName, string targetPath);
}

internal sealed class WindowsDirectChildJunction : IDirectChildJunction
{
    private static readonly StringComparer PathComparer =
        StringComparer.OrdinalIgnoreCase;

    private readonly string _trustedRoot;
    private readonly string _trustedRootPrefix;
    private readonly IWindowsDirectChildJunctionPlatform _platform;

    public WindowsDirectChildJunction(string trustedRoot)
        : this(trustedRoot, new Win32DirectChildJunctionPlatform())
    {
    }

    internal WindowsDirectChildJunction(
        string trustedRoot,
        IWindowsDirectChildJunctionPlatform platform)
    {
        _trustedRoot = NormalizeAbsolutePath(trustedRoot, nameof(trustedRoot));
        _trustedRootPrefix = AppendDirectorySeparator(_trustedRoot);
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public void VerifyInactive(
        string savesRoot,
        string slotName,
        string targetPath)
    {
        OperationPaths paths = PreparePaths(savesRoot, slotName, targetPath);
        WindowsDirectChildEntry entry = _platform.Inspect(paths.SlotPath);
        if (entry.Kind != WindowsDirectChildEntryKind.Missing)
        {
            throw new InvalidOperationException(
                $"The exact Stardew test-save slot already exists: {paths.SlotPath}");
        }
    }

    public string Activate(
        string savesRoot,
        string slotName,
        string targetPath)
    {
        OperationPaths paths = PreparePaths(savesRoot, slotName, targetPath);
        RequirePlainTrustedTarget(paths.TargetPath);

        WindowsDirectChildEntry collision = _platform.Inspect(paths.SlotPath);
        if (collision.Kind != WindowsDirectChildEntryKind.Missing)
        {
            throw new InvalidOperationException(
                $"The exact Stardew test-save slot already exists: {paths.SlotPath}");
        }

        _platform.CreateDirectoryJunction(paths.SlotPath, paths.TargetPath);
        try
        {
            VerifyActive(savesRoot, slotName, targetPath);
        }
        catch (Exception verificationException)
        {
            try
            {
                EnsureInactive(savesRoot, slotName, targetPath);
            }
            catch (Exception cleanupException)
            {
                throw new InvalidOperationException(
                    $"The created Stardew test-save junction failed verification and could not be safely removed: {cleanupException.Message}",
                    new AggregateException(verificationException, cleanupException));
            }

            throw;
        }

        return paths.SlotPath;
    }

    public void VerifyActive(
        string savesRoot,
        string slotName,
        string targetPath)
    {
        OperationPaths paths = PreparePaths(savesRoot, slotName, targetPath);
        RequirePlainTrustedTarget(paths.TargetPath);
        RequireExactJunction(
            paths,
            _platform.Inspect(paths.SlotPath),
            "is not the expected active Stardew test-save junction");
    }

    public void EnsureInactive(
        string savesRoot,
        string slotName,
        string targetPath)
    {
        OperationPaths paths = PreparePaths(savesRoot, slotName, targetPath);
        WindowsDirectChildEntry entry = _platform.Inspect(paths.SlotPath);
        if (entry.Kind == WindowsDirectChildEntryKind.Missing)
        {
            return;
        }

        RequireExactJunction(
            paths,
            entry,
            "is occupied by an entry SDVKit does not own");
        _platform.DeleteExactDirectoryJunction(
            paths.SlotPath,
            paths.TargetPath);

        WindowsDirectChildEntry afterDelete = _platform.Inspect(paths.SlotPath);
        if (afterDelete.Kind != WindowsDirectChildEntryKind.Missing)
        {
            throw new InvalidOperationException(
                $"The exact Stardew test-save junction remained after cleanup: {paths.SlotPath}");
        }
    }

    private OperationPaths PreparePaths(
        string savesRoot,
        string slotName,
        string targetPath)
    {
        string normalizedSavesRoot = NormalizeAbsolutePath(
            savesRoot,
            nameof(savesRoot));
        ValidateSlotName(slotName);

        string slotPath = NormalizeAbsolutePath(
            Path.Combine(normalizedSavesRoot, slotName),
            nameof(slotName));
        string? slotParent = Path.GetDirectoryName(slotPath);
        if (slotParent is null
            || !PathComparer.Equals(
                NormalizeAbsolutePath(slotParent, nameof(slotName)),
                normalizedSavesRoot))
        {
            throw new ArgumentException(
                "The Stardew test-save slot must be one direct child of the saves root.",
                nameof(slotName));
        }

        string normalizedTarget = NormalizeAbsolutePath(
            targetPath,
            nameof(targetPath));
        if (!normalizedTarget.StartsWith(
                _trustedRootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Stardew test-save target must be below the trusted fixture root.",
                nameof(targetPath));
        }

        return new OperationPaths(slotPath, normalizedTarget);
    }

    private void RequirePlainTrustedTarget(string targetPath)
    {
        RequirePlainDirectory(_trustedRoot);

        string relativeTarget = targetPath[_trustedRootPrefix.Length..];
        string current = _trustedRoot;
        foreach (string component in relativeTarget.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RequirePlainDirectory(current);
        }
    }

    private void RequirePlainDirectory(string path)
    {
        WindowsDirectChildEntry entry = _platform.Inspect(path);
        if (entry.Kind != WindowsDirectChildEntryKind.PlainDirectory)
        {
            throw new InvalidOperationException(
                $"The trusted Stardew test-save target path must contain only existing plain directories: {path}");
        }
    }

    private static void RequireExactJunction(
        OperationPaths paths,
        WindowsDirectChildEntry entry,
        string failure)
    {
        if (entry.Kind == WindowsDirectChildEntryKind.DirectoryJunction
            && IsSameNormalizedPath(entry.JunctionTarget, paths.TargetPath))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The exact Stardew test-save slot {failure}: {paths.SlotPath}");
    }

    internal static bool IsSameNormalizedPath(
        string? candidate,
        string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            return PathComparer.Equals(
                NormalizeAbsolutePath(candidate, nameof(candidate)),
                expected);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "The path must be fully qualified.",
                parameterName);
        }

        RejectTraversalComponents(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectTraversalComponents(
        string path,
        string parameterName)
    {
        foreach (string component in path.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
            {
                throw new ArgumentException(
                    "The path must not contain traversal components.",
                    parameterName);
            }
        }
    }

    private static void ValidateSlotName(string slotName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        if (slotName is "." or ".."
            || slotName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || slotName.Contains(Path.DirectorySeparatorChar)
            || slotName.Contains(Path.AltDirectorySeparatorChar)
            || slotName.Contains(Path.VolumeSeparatorChar))
        {
            throw new ArgumentException(
                "The Stardew test-save slot name must be exactly one plain path component.",
                nameof(slotName));
        }
    }

    private static string AppendDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path)
            ? path
            : path + Path.DirectorySeparatorChar;

    private readonly record struct OperationPaths(
        string SlotPath,
        string TargetPath);
}

internal enum WindowsDirectChildEntryKind
{
    Missing,
    PlainFile,
    PlainDirectory,
    DirectoryJunction,
    OtherReparsePoint,
}

internal readonly record struct WindowsDirectChildEntry(
    WindowsDirectChildEntryKind Kind,
    string? JunctionTarget = null);

internal interface IWindowsDirectChildJunctionPlatform
{
    WindowsDirectChildEntry Inspect(string path);

    void CreateDirectoryJunction(string junctionPath, string targetPath);

    void DeleteExactDirectoryJunction(string junctionPath, string expectedTargetPath);
}

internal sealed class Win32DirectChildJunctionPlatform
    : IWindowsDirectChildJunctionPlatform
{
    private const uint InvalidFileAttributes = uint.MaxValue;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FileDispositionInfoClass = 4;
    private const int MaximumReparseDataBufferSize = 16 * 1024;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    public WindowsDirectChildEntry Inspect(string path)
    {
        EnsureWindows();
        uint attributes = GetFileAttributes(path);
        if (attributes == InvalidFileAttributes)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return new WindowsDirectChildEntry(
                    WindowsDirectChildEntryKind.Missing);
            }

            throw CreateWindowsException(
                "Windows could not inspect the exact test-save path",
                path,
                error);
        }

        bool isDirectory = (attributes & FileAttributeDirectory) != 0;
        if ((attributes & FileAttributeReparsePoint) == 0)
        {
            return new WindowsDirectChildEntry(
                isDirectory
                    ? WindowsDirectChildEntryKind.PlainDirectory
                    : WindowsDirectChildEntryKind.PlainFile);
        }

        using SafeFileHandle handle = OpenReparsePoint(
            path,
            desiredAccess: 0,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        return ReadReparsePoint(handle, path, isDirectory);
    }

    public void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        EnsureWindows();
        if (!CreateDirectory(junctionPath, IntPtr.Zero))
        {
            throw CreateWindowsException(
                "Windows could not create the exact test-save junction directory",
                junctionPath,
                Marshal.GetLastWin32Error());
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = OpenReparsePoint(
                junctionPath,
                GenericWrite | DeleteAccess,
                FileShare.Read);
            byte[] buffer = CreateMountPointBuffer(targetPath);
            if (!DeviceIoControlSet(
                    handle,
                    FsctlSetReparsePoint,
                    buffer,
                    checked((uint)buffer.Length),
                    IntPtr.Zero,
                    outputBufferSize: 0,
                    out _,
                    IntPtr.Zero))
            {
                throw CreateWindowsException(
                    "Windows could not activate the exact test-save junction",
                    junctionPath,
                    Marshal.GetLastWin32Error());
            }
        }
        catch (Exception creationException)
        {
            if (handle is not null)
            {
                try
                {
                    MarkForDeletion(handle, junctionPath);
                }
                catch (Exception cleanupException)
                {
                    throw new InvalidOperationException(
                        $"Windows could not clean up the failed test-save junction directory: {junctionPath}",
                        new AggregateException(
                            creationException,
                            cleanupException));
                }
            }

            throw;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public void DeleteExactDirectoryJunction(
        string junctionPath,
        string expectedTargetPath)
    {
        EnsureWindows();
        using SafeFileHandle handle = OpenReparsePoint(
            junctionPath,
            DeleteAccess,
            FileShare.Read);
        WindowsDirectChildEntry entry = ReadReparsePoint(
            handle,
            junctionPath,
            isDirectory: true);
        if (entry.Kind != WindowsDirectChildEntryKind.DirectoryJunction
            || !WindowsDirectChildJunction.IsSameNormalizedPath(
                entry.JunctionTarget,
                expectedTargetPath))
        {
            throw new InvalidOperationException(
                $"The exact Stardew test-save slot changed before handle-bound cleanup: {junctionPath}");
        }

        MarkForDeletion(handle, junctionPath);
    }

    private static SafeFileHandle OpenReparsePoint(
        string path,
        uint desiredAccess,
        FileShare shareMode)
    {
        SafeFileHandle handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw CreateWindowsException(
            "Windows could not open the exact test-save reparse point",
            path,
            error);
    }

    private static WindowsDirectChildEntry ReadReparsePoint(
        SafeFileHandle handle,
        string path,
        bool isDirectory)
    {
        byte[] buffer = new byte[MaximumReparseDataBufferSize];
        if (!DeviceIoControlGet(
                handle,
                FsctlGetReparsePoint,
                IntPtr.Zero,
                inputBufferSize: 0,
                buffer,
                checked((uint)buffer.Length),
                out uint bytesReturned,
                IntPtr.Zero))
        {
            throw CreateWindowsException(
                "Windows could not read the exact test-save reparse point",
                path,
                Marshal.GetLastWin32Error());
        }

        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (!isDirectory || tag != IoReparseTagMountPoint)
        {
            return new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.OtherReparsePoint);
        }

        string target = ReadMountPointTarget(buffer, bytesReturned, path);
        return new WindowsDirectChildEntry(
            WindowsDirectChildEntryKind.DirectoryJunction,
            target);
    }

    private static void MarkForDeletion(
        SafeFileHandle handle,
        string path)
    {
        var disposition = new FileDispositionInformation(deleteFile: true);
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                checked((uint)Marshal.SizeOf<FileDispositionInformation>())))
        {
            throw CreateWindowsException(
                "Windows could not mark the exact test-save junction handle for deletion",
                path,
                Marshal.GetLastWin32Error());
        }
    }

    private static byte[] CreateMountPointBuffer(string targetPath)
    {
        string substituteName = targetPath.StartsWith(
                Path.DirectorySeparatorChar + new string(Path.DirectorySeparatorChar, 1),
                StringComparison.Ordinal)
            ? @"\??\UNC\" + targetPath.TrimStart(Path.DirectorySeparatorChar)
            : @"\??\" + targetPath;
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        byte[] printBytes = Encoding.Unicode.GetBytes(targetPath);
        int printOffset = checked(substituteBytes.Length + sizeof(char));
        int dataLength = checked(
            8
            + substituteBytes.Length
            + sizeof(char)
            + printBytes.Length
            + sizeof(char));
        int bufferLength = checked(8 + dataLength);
        if (dataLength > ushort.MaxValue
            || bufferLength > MaximumReparseDataBufferSize)
        {
            throw new PathTooLongException(
                "The test-save junction target is too long for a Windows mount point.");
        }

        byte[] buffer = new byte[bufferLength];
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(0, sizeof(uint)),
            IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(4, sizeof(ushort)),
            checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(8, sizeof(ushort)),
            0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(10, sizeof(ushort)),
            checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(12, sizeof(ushort)),
            checked((ushort)printOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(14, sizeof(ushort)),
            checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, checked(16 + printOffset));
        return buffer;
    }

    private static string ReadMountPointTarget(
        byte[] buffer,
        uint bytesReturned,
        string path)
    {
        if (bytesReturned < 16)
        {
            throw InvalidMountPoint(path);
        }

        int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(
            buffer.AsSpan(4, sizeof(ushort)));
        if (dataLength < 8 || checked(8 + dataLength) > bytesReturned)
        {
            throw InvalidMountPoint(path);
        }

        int substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            buffer.AsSpan(8, sizeof(ushort)));
        int substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(
            buffer.AsSpan(10, sizeof(ushort)));
        int pathBufferLength = dataLength - 8;
        if ((substituteOffset & 1) != 0
            || (substituteLength & 1) != 0
            || substituteOffset > pathBufferLength
            || substituteLength > pathBufferLength - substituteOffset)
        {
            throw InvalidMountPoint(path);
        }

        string substituteName = Encoding.Unicode.GetString(
            buffer,
            checked(16 + substituteOffset),
            substituteLength);
        const string nativeUncPrefix = @"\??\UNC\";
        const string nativePrefix = @"\??\";
        if (substituteName.StartsWith(
                nativeUncPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + substituteName[nativeUncPrefix.Length..];
        }

        return substituteName.StartsWith(
                nativePrefix,
                StringComparison.OrdinalIgnoreCase)
            ? substituteName[nativePrefix.Length..]
            : substituteName;
    }

    private static InvalidOperationException InvalidMountPoint(string path) =>
        new($"Windows returned invalid mount-point data for the exact test-save junction: {path}");

    private static InvalidOperationException CreateWindowsException(
        string action,
        string path,
        int error) =>
        new($"{action}: {path}. {new Win32Exception(error).Message} (Win32 {error}).");

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Stardew test-save junction is implemented for Windows only.");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct FileDispositionInformation
    {
        public FileDispositionInformation(bool deleteFile)
        {
            DeleteFile = deleteFile ? (byte)1 : (byte)0;
        }

        public readonly byte DeleteFile;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileAttributesW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(
        string path,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        uint fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlGet(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        [Out] byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlSet(
        SafeFileHandle device,
        uint controlCode,
        [In] byte[] inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}

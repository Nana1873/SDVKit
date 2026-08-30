using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.Cli.LiveLab;

internal static class ProjectPathCanonicalizer
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    public static string CanonicalizeExistingDirectory(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)
            || !Path.IsPathFullyQualified(projectRoot))
        {
            throw new InvalidOperationException(
                "The project root must be a fully qualified existing directory.");
        }

        string absolutePath;
        try
        {
            absolutePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"The project root path is invalid: {projectRoot}",
                exception);
        }

        if (!Directory.Exists(absolutePath))
        {
            throw new InvalidOperationException(
                $"The project root directory was not found: {absolutePath}");
        }

        if (!OperatingSystem.IsWindows())
        {
            return absolutePath;
        }

        using SafeFileHandle handle = CreateFile(
            absolutePath,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Windows could not open the project root directory '{absolutePath}': "
                + $"{new Win32Exception(error).Message} (Win32 {error}).");
        }

        string finalPath = RemoveExtendedPrefix(ReadFinalDosPath(handle, absolutePath));
        try
        {
            if (!Path.IsPathFullyQualified(finalPath))
            {
                throw new InvalidOperationException(
                    $"Windows returned a non-DOS project-root path for '{absolutePath}'.");
            }

            string normalizedFinalPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(finalPath));
            if (!Directory.Exists(normalizedFinalPath))
            {
                throw new InvalidOperationException(
                    $"The final project root is not an existing directory: {normalizedFinalPath}");
            }

            return normalizedFinalPath;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Windows returned an invalid final project-root path for '{absolutePath}'.",
                exception);
        }
    }

    private static string ReadFinalDosPath(SafeFileHandle handle, string originalPath)
    {
        char[] buffer = new char[260];
        uint length = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Length),
            FileNameNormalized | VolumeNameDos);
        if (length == 0)
        {
            ThrowFinalPathError(originalPath);
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length)];
            length = GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Length),
                FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                ThrowFinalPathError(originalPath);
            }

            if (length >= buffer.Length)
            {
                throw new InvalidOperationException(
                    $"Windows returned an unstable final project-root path for '{originalPath}'.");
            }
        }

        return new string(buffer, 0, checked((int)length));
    }

    private static void ThrowFinalPathError(string originalPath)
    {
        int error = Marshal.GetLastWin32Error();
        throw new InvalidOperationException(
            $"Windows could not resolve the final project-root path '{originalPath}': "
            + $"{new Win32Exception(error).Message} (Win32 {error}).");
    }

    private static string RemoveExtendedPrefix(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";

        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);
}

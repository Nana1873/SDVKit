using System.Buffers.Binary;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class ModBuildIdentity
{
    private const string RuntimeConfigFileName = "config.json";

    private static readonly string[] RequiredFiles =
    [
        "SdvKit.AlwaysOn.dll",
        "manifest.json",
    ];

    public static string Compute(string modPath)
    {
        if (string.IsNullOrWhiteSpace(modPath))
        {
            throw new ArgumentException("The mod path is required.", nameof(modPath));
        }

        string absoluteModPath = Path.GetFullPath(modPath);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        byte[] buffer = new byte[81920];
        foreach (string fileName in RequiredFiles)
        {
            string path = Path.Combine(absoluteModPath, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The declared AlwaysOn build is missing {fileName}.",
                    path);
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            byte[] name = Encoding.UTF8.GetBytes(fileName);
            BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
            hash.AppendData(name);
            hash.AppendData([0]);
            hash.AppendData(length);

            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    public static string ComputeFileSet(string rootPath)
    {
        FileSetEntry[] files = InspectFileSet(rootPath);
        (string fullIdentity, _) = ComputeFileSetIdentities(
            files,
            includeWithoutRuntimeConfig: false);
        return fullIdentity;
    }

    internal static string ComputeFileSetWithReplacements(string rootPath, IReadOnlyDictionary<string, string> replacements)
    {
        FileSetEntry[] files = InspectFileSet(rootPath);
        if (replacements.Keys.Any(key => !files.Any(file => file.RelativePath == key)))
            throw new InvalidDataException("Refresh replacements must name existing staged files exactly.");
        files = files.Select(file => replacements.TryGetValue(file.RelativePath, out string? replacement)
            ? new FileSetEntry(replacement, file.RelativePath) : file).ToArray();
        return ComputeFileSetIdentities(files, includeWithoutRuntimeConfig: false).Item1;
    }

    public static bool MatchesFileSet(
        string rootPath,
        string expectedIdentity,
        bool allowNewRootConfigJson)
    {
        if (!IsValid(expectedIdentity))
        {
            return false;
        }

        FileSetEntry[] files = InspectFileSet(rootPath);
        bool hasExactRootConfig = files.Any(file => string.Equals(
            file.RelativePath,
            RuntimeConfigFileName,
            StringComparison.Ordinal));
        (string fullIdentity, string? identityWithoutRuntimeConfig) =
            ComputeFileSetIdentities(
                files,
                includeWithoutRuntimeConfig: allowNewRootConfigJson
                    && hasExactRootConfig);
        return string.Equals(fullIdentity, expectedIdentity, StringComparison.Ordinal)
            || (identityWithoutRuntimeConfig is not null
                && string.Equals(
                    identityWithoutRuntimeConfig,
                    expectedIdentity,
                    StringComparison.Ordinal));
    }

    public static string ComputeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The file path is required.", nameof(path));
        }

        string absolutePath = GetFullPath(path, "The file path is invalid.");
        FileAttributes attributes = GetAttributes(
            absolutePath,
            "The file could not be inspected.");
        RejectRegularFileViolation(absolutePath, attributes);

        try
        {
            using FileStream stream = OpenReadOnly(absolutePath);
            using SHA256 hash = SHA256.Create();
            return FormatHash(hash.ComputeHash(stream));
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The file could not be hashed: {absolutePath}",
                exception);
        }
    }

    public static bool IsValid(string? value)
    {
        const int Sha256HexLength = 64;
        if (value is null
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Length != "sha256:".Length + Sha256HexLength)
        {
            return false;
        }

        return value["sha256:".Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static FileSetEntry[] InspectFileSet(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("The file-set root is required.", nameof(rootPath));
        }

        string absoluteRoot = GetFullPath(rootPath, "The file-set root path is invalid.");
        FileAttributes rootAttributes = GetAttributes(
            absoluteRoot,
            "The file-set root could not be inspected.");
        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                $"The file-set root is not a directory: {absoluteRoot}");
        }

        RejectReparsePoint(absoluteRoot, rootAttributes);
        FileSetEntry[] files = EnumerateRegularFiles(absoluteRoot);
        if (files.Length == 0)
        {
            throw new InvalidDataException(
                $"The file-set root does not contain any regular files: {absoluteRoot}");
        }

        return files;
    }

    private static FileSetEntry[] EnumerateRegularFiles(string absoluteRoot)
    {
        var files = new List<FileSetEntry>();
        var pending = new Stack<string>();
        pending.Push(absoluteRoot);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            FileAttributes directoryAttributes = GetAttributes(
                directory,
                "A file-set directory could not be inspected.");
            if ((directoryAttributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    $"A file-set directory changed type while it was inspected: {directory}");
            }

            RejectReparsePoint(directory, directoryAttributes);
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException
                or SecurityException
                or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"A file-set directory could not be enumerated: {directory}",
                    exception);
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes = GetAttributes(
                    entry,
                    "A file-set entry could not be inspected.");
                RejectReparsePoint(entry, attributes);
                string relativePath = NormalizeRelativePath(absoluteRoot, entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (string.Equals(
                            relativePath,
                            RuntimeConfigFileName,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The root config.json entry is not a regular file.");
                    }

                    pending.Push(entry);
                    continue;
                }

                RejectRegularFileViolation(entry, attributes);
                files.Add(new FileSetEntry(entry, relativePath));
            }
        }

        return files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string FullIdentity, string? IdentityWithoutRuntimeConfig)
        ComputeFileSetIdentities(
            IReadOnlyList<FileSetEntry> files,
            bool includeWithoutRuntimeConfig)
    {
        using IncrementalHash fullHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash? hashWithoutRuntimeConfig = includeWithoutRuntimeConfig
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        Span<byte> length = stackalloc byte[sizeof(long)];
        byte[] buffer = new byte[81920];
        foreach (FileSetEntry file in files)
        {
            bool appendToAlternative = hashWithoutRuntimeConfig is not null
                && !string.Equals(
                    file.RelativePath,
                    RuntimeConfigFileName,
                    StringComparison.Ordinal);
            AppendFile(
                fullHash,
                appendToAlternative ? hashWithoutRuntimeConfig : null,
                length,
                buffer,
                file);
        }

        return (
            FormatHash(fullHash.GetHashAndReset()),
            hashWithoutRuntimeConfig is null
                ? null
                : FormatHash(hashWithoutRuntimeConfig.GetHashAndReset()));
    }

    private static void AppendFile(
        IncrementalHash hash,
        IncrementalHash? additionalHash,
        Span<byte> length,
        byte[] buffer,
        FileSetEntry file)
    {
        FileAttributes attributes = GetAttributes(
            file.AbsolutePath,
            "A file-set file could not be inspected before hashing.");
        RejectRegularFileViolation(file.AbsolutePath, attributes);

        try
        {
            using FileStream stream = OpenReadOnly(file.AbsolutePath);
            long expectedLength = stream.Length;
            byte[] name = Encoding.UTF8.GetBytes(file.RelativePath);
            BinaryPrimitives.WriteInt64LittleEndian(length, expectedLength);
            hash.AppendData(name);
            hash.AppendData([0]);
            hash.AppendData(length);
            additionalHash?.AppendData(name);
            additionalHash?.AppendData([0]);
            additionalHash?.AppendData(length);

            long totalRead = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                additionalHash?.AppendData(buffer, 0, read);
                totalRead = checked(totalRead + read);
            }

            if (totalRead != expectedLength)
            {
                throw new InvalidDataException(
                    $"A file-set file changed length while it was hashed: {file.AbsolutePath}");
            }
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"A file-set file could not be hashed: {file.AbsolutePath}",
                exception);
        }
    }

    private static FileStream OpenReadOnly(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(root, path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException)
        {
            throw new InvalidDataException(
                $"A file-set path could not be made relative to its root: {path}",
                exception);
        }

        if (relativePath == "."
            || Path.IsPathFullyQualified(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && relativePath.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"A file-set entry resolves outside its root: {path}");
        }

        string normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? normalized
            : normalized.Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string GetFullPath(string path, string message)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static FileAttributes GetAttributes(string path, string message)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{message} Path: {path}", exception);
        }
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The file set contains a reparse point: {path}");
        }
    }

    private static void RejectRegularFileViolation(string path, FileAttributes attributes)
    {
        RejectReparsePoint(path, attributes);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"The file-set entry is not a regular file: {path}");
        }
    }

    private static string FormatHash(byte[] hash)
    {
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private sealed record FileSetEntry(string AbsolutePath, string RelativePath);
}

namespace SdvKit.AlwaysOn;

internal static class ReviewResponseFile
{
    public static void Write(string responsePath, ReadOnlySpan<byte> bytes)
    {
        string absolutePath = Path.GetFullPath(responsePath);
        EnsureRegularDirectory(Path.GetDirectoryName(absolutePath)!);
        string temporaryPath = absolutePath + ".tmp";
        if (EntryExists(absolutePath) || EntryExists(temporaryPath))
        {
            throw new InvalidDataException("The review response target already exists.");
        }

        // CreateNew must succeed before this invocation may clean up the temporary path.
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        var ownsTemporary = true;
        var ownsResponse = false;
        try
        {
            EnsureRegularFile(temporaryPath);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            stream.Close();
            EnsureRegularFile(temporaryPath);
            File.Move(temporaryPath, absolutePath);
            ownsTemporary = false;
            ownsResponse = true;
            EnsureRegularFile(absolutePath);
            ownsResponse = false;
        }
        finally
        {
            stream.Close();
            if (ownsTemporary)
            {
                TryDeleteOwnedRegularFile(temporaryPath);
            }
            if (ownsResponse)
            {
                TryDeleteOwnedRegularFile(absolutePath);
            }
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureRegularDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                "The review response is not a regular file.");
        }
    }

    private static void TryDeleteOwnedRegularFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0
                && (attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            // The unique owned path is already absent.
        }
    }
}

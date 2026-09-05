namespace SdvKit.Cli;

internal sealed class ProjectReviewActionLock : IDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly FileStream _stream;

    private ProjectReviewActionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ProjectReviewActionLock? TryAcquire(string runtimePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
        if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
            || (runtimeAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review action root is not a regular directory.");
        }

        string lockPath = Path.Combine(absoluteRuntimePath, "mcp-action.lock");
        RefuseReparsePoint(lockPath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
            return null;
        }

        try
        {
            RefuseReparsePoint(lockPath);
            return new ProjectReviewActionLock(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    internal void RequireHeldFor(string runtimePath)
    {
        if (_stream.SafeFileHandle.IsClosed || _stream.SafeFileHandle.IsInvalid
            || !string.Equals(_stream.Name, Path.Combine(Path.GetFullPath(runtimePath), "mcp-action.lock"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The caller must hold the exact review action lock.");
    }

    private static void RefuseReparsePoint(string path)
    {
        if (File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The review action lock is a reparse point.");
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        int windowsError = exception.HResult & 0xffff;
        return windowsError is ErrorSharingViolation or ErrorLockViolation;
    }
}

namespace SdvKit.Cli.LiveLab;

internal sealed class LiveLabOperationLock : IDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly FileStream _stream;

    private LiveLabOperationLock(FileStream stream)
    {
        _stream = stream;
    }

    public static LiveLabOperationLock? TryAcquire(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        LiveLabPaths paths = LiveLabPaths.Resolve(projectRoot);
        paths.EnsureDirectories();
        string lockPath = Path.Combine(paths.RuntimePath, "operation.lock");
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
            return new LiveLabOperationLock(stream);
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

    internal void RequireHeldFor(string projectRoot)
    {
        string expected = Path.Combine(LiveLabPaths.Resolve(projectRoot).RuntimePath, "operation.lock");
        if (_stream.SafeFileHandle.IsClosed || _stream.SafeFileHandle.IsInvalid
            || !string.Equals(_stream.Name, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The caller must hold the exact lab operation lock.");
    }

    private static void RefuseReparsePoint(string path)
    {
        if (File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"The live-lab operation lock is a reparse point: {path}");
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        int windowsError = exception.HResult & 0xffff;
        return windowsError is ErrorSharingViolation or ErrorLockViolation;
    }
}

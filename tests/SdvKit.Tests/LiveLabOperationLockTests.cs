using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class LiveLabOperationLockTests
{
    [Fact]
    public void ExactWorkspaceAllowsOnlyOneLifecycleOperationAtATime()
    {
        using TemporaryDirectory temporary = new();
        LiveLabOperationLock first = Assert.IsType<LiveLabOperationLock>(
            LiveLabOperationLock.TryAcquire(temporary.Path));

        try
        {
            Assert.Null(LiveLabOperationLock.TryAcquire(temporary.Path));
        }
        finally
        {
            first.Dispose();
        }

        using LiveLabOperationLock? afterRelease =
            LiveLabOperationLock.TryAcquire(temporary.Path);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public void WorkspaceAliasUsesTheSameFilesystemLockWhenLinksAreSupported()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory aliases = new();
        string alias = Path.Combine(aliases.Path, "project-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, project.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        using LiveLabOperationLock first = Assert.IsType<LiveLabOperationLock>(
            LiveLabOperationLock.TryAcquire(project.Path));

        Assert.Null(LiveLabOperationLock.TryAcquire(alias));
    }
}

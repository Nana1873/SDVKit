using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class NetworkTwoModBuildPreparerTests
{
    [Fact]
    public void BuildsOnceForHostAndCopiesOnlyTheDeclaredBuildToFarmhand()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        var builder = new FakeBuilder();
        var preparer = new NetworkTwoModBuildPreparer(builder);

        NetworkTwoModBuildResult result = preparer.Prepare(
            game.Path,
            hostPaths,
            farmhandPaths);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.NotNull(result.BuildIdentity);
        Assert.True(ModBuildIdentity.IsValid(result.BuildIdentity));
        Assert.True(result.HostBuild.Succeeded);
        Assert.Equal(1, builder.CallCount);
        Assert.Equal(hostPaths.SingleRoot, builder.BuiltPaths.SingleRoot);
        Assert.Equal(
            result.BuildIdentity,
            ModBuildIdentity.Compute(hostPaths.GameSideModPath));
        Assert.Equal(
            result.BuildIdentity,
            ModBuildIdentity.Compute(farmhandPaths.GameSideModPath));
        Assert.Equal(
            ["SdvKit.GameSide.dll", "manifest.json"],
            Directory.GetFileSystemEntries(farmhandPaths.GameSideModPath)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void ReplacesAStaleFarmhandCopyWithoutBuildingAgain()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        farmhandPaths.EnsureDirectories();
        Directory.CreateDirectory(farmhandPaths.GameSideModPath);
        File.WriteAllText(Path.Combine(farmhandPaths.GameSideModPath, "stale.txt"), "stale");
        var builder = new FakeBuilder();
        var preparer = new NetworkTwoModBuildPreparer(builder);

        NetworkTwoModBuildResult result = preparer.Prepare(
            game.Path,
            hostPaths,
            farmhandPaths);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, builder.CallCount);
        Assert.False(File.Exists(Path.Combine(farmhandPaths.GameSideModPath, "stale.txt")));
    }

    [Fact]
    public void FailedHostBuildStopsBeforePreparingFarmhand()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        var builder = new FakeBuilder(succeed: false);
        var preparer = new NetworkTwoModBuildPreparer(builder);

        NetworkTwoModBuildResult result = preparer.Prepare(
            game.Path,
            hostPaths,
            farmhandPaths);

        Assert.False(result.Succeeded);
        Assert.Equal("build failed", result.Error);
        Assert.Null(result.BuildIdentity);
        Assert.Equal(1, builder.CallCount);
        Assert.False(Directory.Exists(farmhandPaths.GameSideModPath));
    }

    [Fact]
    public void SuccessfulBuildWithUndeclaredContentsIsRejectedWithoutACopy()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        var builder = new FakeBuilder(addUndeclaredFile: true);
        var preparer = new NetworkTwoModBuildPreparer(builder);

        NetworkTwoModBuildResult result = preparer.Prepare(
            game.Path,
            hostPaths,
            farmhandPaths);

        Assert.False(result.Succeeded);
        Assert.Contains("must contain only", result.Error, StringComparison.Ordinal);
        Assert.Null(result.BuildIdentity);
        Assert.Equal(1, builder.CallCount);
        Assert.False(Directory.Exists(farmhandPaths.GameSideModPath));
    }

    [Fact]
    public void RejectsSharedHostAndFarmhandPathsBeforeBuilding()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        var builder = new FakeBuilder();
        var preparer = new NetworkTwoModBuildPreparer(builder);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            preparer.Prepare(game.Path, hostPaths, hostPaths));

        Assert.Contains("separate", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, builder.CallCount);
    }

    private sealed class FakeBuilder : IGameSideBuilder
    {
        private readonly bool _succeed;
        private readonly bool _addUndeclaredFile;

        public FakeBuilder(bool succeed = true, bool addUndeclaredFile = false)
        {
            _succeed = succeed;
            _addUndeclaredFile = addUndeclaredFile;
        }

        public int CallCount { get; private set; }

        public LiveLabPaths BuiltPaths { get; private set; } = null!;

        public GameSideBuildResult BuildAndInstall(string gamePath, LiveLabPaths paths)
        {
            CallCount++;
            BuiltPaths = paths;
            if (!_succeed)
            {
                return new GameSideBuildResult(false, "build.log", "build failed");
            }

            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.GameSideModPath);
            File.WriteAllText(
                Path.Combine(paths.GameSideModPath, "SdvKit.GameSide.dll"),
                "same build");
            File.WriteAllText(
                Path.Combine(paths.GameSideModPath, "manifest.json"),
                "{\"UniqueID\":\"SDVKit.GameSide\"}");
            if (_addUndeclaredFile)
            {
                File.WriteAllText(Path.Combine(paths.GameSideModPath, "extra.pdb"), "extra");
            }

            return new GameSideBuildResult(true, "build.log", null);
        }
    }
}

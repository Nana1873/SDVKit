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
            ModBuildIdentity.Compute(hostPaths.AlwaysOnModPath));
        Assert.Equal(
            result.BuildIdentity,
            ModBuildIdentity.Compute(farmhandPaths.AlwaysOnModPath));
        Assert.Equal(
            ["SdvKit.AlwaysOn.dll", "manifest.json"],
            Directory.GetFileSystemEntries(farmhandPaths.AlwaysOnModPath)
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
        Directory.CreateDirectory(farmhandPaths.AlwaysOnModPath);
        File.WriteAllText(Path.Combine(farmhandPaths.AlwaysOnModPath, "stale.txt"), "stale");
        var builder = new FakeBuilder();
        var preparer = new NetworkTwoModBuildPreparer(builder);

        NetworkTwoModBuildResult result = preparer.Prepare(
            game.Path,
            hostPaths,
            farmhandPaths);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, builder.CallCount);
        Assert.False(File.Exists(Path.Combine(farmhandPaths.AlwaysOnModPath, "stale.txt")));
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
        Assert.False(Directory.Exists(farmhandPaths.AlwaysOnModPath));
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
        Assert.False(Directory.Exists(farmhandPaths.AlwaysOnModPath));
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

    private sealed class FakeBuilder : IAlwaysOnBuilder
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

        public AlwaysOnBuildResult BuildAndInstall(string gamePath, LiveLabPaths paths)
        {
            CallCount++;
            BuiltPaths = paths;
            if (!_succeed)
            {
                return new AlwaysOnBuildResult(false, "build.log", "build failed");
            }

            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.AlwaysOnModPath);
            File.WriteAllText(
                Path.Combine(paths.AlwaysOnModPath, "SdvKit.AlwaysOn.dll"),
                "same build");
            File.WriteAllText(
                Path.Combine(paths.AlwaysOnModPath, "manifest.json"),
                "{\"UniqueID\":\"SDVKit.AlwaysOn\"}");
            if (_addUndeclaredFile)
            {
                File.WriteAllText(Path.Combine(paths.AlwaysOnModPath, "extra.pdb"), "extra");
            }

            return new AlwaysOnBuildResult(true, "build.log", null);
        }
    }
}

using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class LiveLabStorageTests
{
    [Fact]
    public void ResolveIsReadOnlyAndReturnsAbsoluteSingleLabPaths()
    {
        using TemporaryDirectory project = new();

        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        Assert.Equal(
            Path.Combine(project.Path, ".sdvkit", "lab", "single"),
            paths.SingleRoot);
        Assert.Equal(Path.Combine(paths.SingleRoot, "mods"), paths.ModsPath);
        Assert.Equal(Path.Combine(paths.SingleRoot, "runtime"), paths.RuntimePath);
        Assert.Equal(Path.Combine(paths.SingleRoot, "build"), paths.BuildPath);
        Assert.Equal(Path.Combine(paths.SingleRoot, "test-save"), paths.TestSaveRoot);
        Assert.Equal(
            Path.Combine(paths.TestSaveRoot, "fixture.json"),
            paths.TestSaveManifestPath);
        Assert.Equal(Path.Combine(paths.TestSaveRoot, "work"), paths.TestSaveWorkPath);
        Assert.Equal(
            Path.Combine(paths.TestSaveRoot, "baseline"),
            paths.TestSaveBaselinePath);
        Assert.Equal(
            Path.Combine(paths.RuntimePath, "test-save-scenario.log"),
            paths.TestSaveScenarioLogPath);
        Assert.True(Path.IsPathFullyQualified(paths.StatePath));
        Assert.True(Path.IsPathFullyQualified(paths.StatusPath));
        Assert.Equal(
            Path.Combine(paths.RuntimePath, "stop.request"),
            paths.StopRequestPath);
        Assert.True(Path.IsPathFullyQualified(paths.StopRequestPath));
        Assert.Equal(
            Path.Combine(paths.RuntimePath, "smapi.stdout.log"),
            paths.StandardOutputPath);
        Assert.Equal(
            Path.Combine(paths.RuntimePath, "smapi.stderr.log"),
            paths.StandardErrorPath);
        Assert.True(Path.IsPathFullyQualified(paths.StandardOutputPath));
        Assert.True(Path.IsPathFullyQualified(paths.StandardErrorPath));
        Assert.False(Directory.Exists(Path.Combine(project.Path, ".sdvkit")));
    }

    [Fact]
    public void EnsureDirectoriesCreatesOnlyTheManagedSingleLabTopology()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.ModsPath));
        Assert.True(Directory.Exists(paths.RuntimePath));
        Assert.True(Directory.Exists(paths.BuildPath));
        Assert.Equal(
            ["build", "mods", "runtime"],
            Directory.GetDirectories(paths.SingleRoot)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void ResolveFailsClosedWhenManagedRootIsAFile()
    {
        using TemporaryDirectory project = new();
        project.WriteFile(".sdvkit", "not a directory");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => LiveLabPaths.Resolve(project.Path));

        Assert.Contains("not a directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFailsClosedForAReparsePointBelowSingleRootWhenSupported()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory target = new();
        string singleRoot = Path.Combine(project.Path, ".sdvkit", "lab", "single");
        Directory.CreateDirectory(singleRoot);
        string link = Path.Combine(singleRoot, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, target.Path);
        }
        catch (Exception creationException) when (creationException is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => LiveLabPaths.Resolve(project.Path));

        Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceAliasResolvesTheSameLifecyclePathsWhenLinksAreSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

        LiveLabPaths direct = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths throughAlias = LiveLabPaths.Resolve(alias);

        Assert.Equal(direct.ProjectRoot, throughAlias.ProjectRoot, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(direct.StatePath, throughAlias.StatePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            direct.StopRequestPath,
            throughAlias.StopRequestPath,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void StateStoreAtomicallyReplacesCamelCaseState()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        LiveLabState first = CreateState(paths, Guid.NewGuid().ToString("N"), 111);
        LiveLabState second = CreateState(paths, Guid.NewGuid().ToString("N"), 222);

        store.Write(first);
        store.Write(second);

        Assert.Equal(second, store.Read());
        Assert.Empty(Directory.EnumerateFiles(paths.RuntimePath, "*.tmp"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(paths.StatePath));
        JsonElement root = document.RootElement;
        Assert.Equal(
            [
                "schemaVersion",
                "topology",
                "launchId",
                "ownedProcessIdentity",
                "modsPath",
                "statusPath",
                "stopRequestPath",
            ],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("single", root.GetProperty("topology").GetString());
        Assert.Equal(222, root
            .GetProperty("ownedProcessIdentity")
            .GetProperty("processId")
            .GetInt32());
    }

    [Fact]
    public void StateWriteProbeUsesRuntimeDirectoryWithoutChangingExistingState()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        LiveLabState state = CreateState(paths, Guid.NewGuid().ToString("N"), 111);
        store.Write(state);

        store.VerifyWritable();

        Assert.Equal(state, store.Read());
        Assert.Empty(Directory.EnumerateFiles(
            paths.RuntimePath,
            ".state-write-probe.*"));
    }

    [Fact]
    public void StateStoreRoundTripsTheExactTestSaveLaunchBinding()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            "11111111111111111111111111111111",
            "22222222222222222222222222222222",
            123456789L,
            "SDVKit_123456789",
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        LiveLabState state = CreateState(paths, Guid.NewGuid().ToString("N"), 111) with
        {
            TestSave = new TestSaveLaunchState(
                TestSaveContract.ScenarioMode,
                identity,
                Path.Combine(project.Path, "exact-saves", identity.SaveId),
                paths.TestSaveWorkPath,
                paths.TestSaveScenarioLogPath),
        };

        store.Write(state);

        Assert.Equal(state, store.Read());
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(paths.StatePath));
        JsonElement testSave = document.RootElement.GetProperty("testSave");
        Assert.Equal(TestSaveContract.ScenarioMode, testSave.GetProperty("mode").GetString());
        Assert.Equal(identity.FixtureId, testSave
            .GetProperty("identity")
            .GetProperty("fixtureId")
            .GetString());
        Assert.Equal(paths.TestSaveWorkPath, testSave.GetProperty("workPath").GetString());
    }

    [Fact]
    public void StateStoreRejectsAnUnknownTopology()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        paths.EnsureDirectories();
        var store = new JsonLiveLabStateStore(paths.StatePath);
        LiveLabState invalid = CreateState(paths, Guid.NewGuid().ToString("N"), 123) with
        {
            Topology = "multiplayer",
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => store.Write(invalid));

        Assert.Contains("topology", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.StatePath));
    }

    [Fact]
    public void DeleteIsIdempotentAndDoesNotCreateTheRuntimeDirectory()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var store = new JsonLiveLabStateStore(paths.StatePath);

        store.Delete();

        Assert.False(Directory.Exists(paths.RuntimePath));
    }

    private static LiveLabState CreateState(
        LiveLabPaths paths,
        string launchId,
        int processId)
    {
        return new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            LiveLabState.SingleTopology,
            launchId,
            new OwnedProcessIdentity(
                processId,
                new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(paths.ProjectRoot, "StardewModdingAPI.exe")),
            paths.ModsPath,
            paths.StatusPath,
            paths.StopRequestPath);
    }
}

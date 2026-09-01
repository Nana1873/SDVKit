using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class TestSaveFixtureStoreTests
{
    private const string WorkspaceId = "11111111111111111111111111111111";
    private const string FixtureId = "22222222222222222222222222222222";
    private const string LaunchId = "33333333333333333333333333333333";

    [Fact]
    public void FirstPreparationRegistersOnlyOneProjectOwnedCreateFixture()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);

        TestSavePreparation preparation = store.PrepareForStart();

        TestSaveLaunchState launch = preparation.LaunchState;
        Assert.Equal(TestSaveContract.CreateMode, launch.Mode);
        Assert.Equal(WorkspaceId, launch.Identity.WorkspaceOwnerId);
        Assert.Equal(FixtureId, launch.Identity.FixtureId);
        Assert.Equal(123456789L, launch.Identity.UniqueGameId);
        Assert.Equal("SDVKit_123456789", launch.Identity.SaveId);
        Assert.Equal(paths.TestSaveWorkPath, launch.WorkPath);
        Assert.StartsWith(paths.SingleRoot, launch.WorkPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.SingleRoot, paths.TestSaveManifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(paths.TestSaveManifestPath));
        Assert.True(File.Exists(Path.Combine(
            paths.TestSaveWorkPath,
            TestSaveContract.FixtureMarkerFileName)));
        Assert.True(junction.Active);
        Assert.Equal(saves.Path, junction.SavesRoot);
        Assert.Equal(launch.Identity.SaveId, junction.SlotName);
        Assert.Equal(paths.TestSaveWorkPath, junction.TargetPath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(saves.Path));
    }

    [Fact]
    public void RetainedExactSlotBlocksARepeatedPreparationBeforeWorkMutation()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState first = store.PrepareForStart().LaunchState;
        string sentinelPath = Path.Combine(paths.TestSaveWorkPath, "active-sentinel");
        File.WriteAllText(sentinelPath, "still in use");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            store.PrepareForStart);

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.True(junction.Active);
        Assert.Equal("still in use", File.ReadAllText(sentinelPath));
        Assert.Equal(first.Identity, ReadIdentity(paths.TestSaveManifestPath));
        Assert.Equal(2, junction.VerifyInactiveCount);
    }

    [Fact]
    public void CreatedFixtureBecomesBaselineAndEveryLaterPreparationRestoresIt()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId), "baseline-save");
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, "SaveGameInfo"), "baseline-info");
        paths.EnsureDirectories();
        File.WriteAllText(paths.StandardOutputPath, "create stdout");
        File.WriteAllText(paths.TestSaveScenarioLogPath, "create log");

        TestSaveCleanupResult created = store.CompleteStopped(create, LaunchId);

        Assert.False(junction.Active);
        Assert.Equal("baseline-save", File.ReadAllText(Path.Combine(
            paths.TestSaveBaselinePath,
            create.Identity.SaveId)));
        Assert.Equal("baseline-info", File.ReadAllText(Path.Combine(
            paths.TestSaveBaselinePath,
            "SaveGameInfo")));
        Assert.Equal("baseline-info", File.ReadAllText(Path.Combine(
            paths.TestSaveWorkPath,
            "SaveGameInfo")));
        Assert.All(created.ArchivedLogPaths, path =>
            Assert.StartsWith(paths.TestSaveRoot, path, StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId), "mutated");
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, "unexpected"), "remove me");

        TestSaveLaunchState scenario = store.PrepareForStart().LaunchState;

        Assert.Equal(TestSaveContract.ScenarioMode, scenario.Mode);
        Assert.Equal(create.Identity, scenario.Identity);
        Assert.Equal("baseline-save", File.ReadAllText(Path.Combine(
            paths.TestSaveWorkPath,
            scenario.Identity.SaveId)));
        Assert.Equal("baseline-info", File.ReadAllText(Path.Combine(
            paths.TestSaveWorkPath,
            "SaveGameInfo")));
        Assert.False(File.Exists(Path.Combine(paths.TestSaveWorkPath, "unexpected")));
        Assert.True(junction.Active);

        store.CompleteStopped(scenario, "44444444444444444444444444444444");

        Assert.False(junction.Active);
        Assert.Equal("baseline-save", File.ReadAllText(Path.Combine(
            paths.TestSaveWorkPath,
            scenario.Identity.SaveId)));
        Assert.Equal("baseline-info", File.ReadAllText(Path.Combine(
            paths.TestSaveWorkPath,
            "SaveGameInfo")));
        Assert.Empty(Directory.EnumerateDirectories(
            paths.TestSaveRoot,
            ".*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ReviewWithoutABaselineFailsWithTheExistingPreparationCommand()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, new FakeJunction());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => store.PrepareReviewForStart(resetFromBaseline: false));

        Assert.Contains(
            "sdvkit lab test-save --topology single --json",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.TestSaveBaselinePath));
        Assert.False(Directory.Exists(paths.TestSaveWorkPath));
    }

    [Fact]
    public void ReviewStartCanResetThenCleanStopPreservesWorkForResume()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        WriteCompleteSavePayload(paths, create.Identity, "baseline save", "baseline info");
        store.CompleteStopped(create, LaunchId);
        string savePath = Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId);
        string infoPath = Path.Combine(paths.TestSaveWorkPath, "SaveGameInfo");
        string reviewOnlyPath = Path.Combine(paths.TestSaveWorkPath, "review-only");
        File.WriteAllText(savePath, "discard before first review");
        File.WriteAllText(reviewOnlyPath, "discard before first review");

        TestSaveLaunchState first = store.PrepareReviewForStart(resetFromBaseline: true)
            .LaunchState;

        Assert.Equal(TestSaveContract.ReviewMode, first.Mode);
        Assert.Equal("baseline save", File.ReadAllText(savePath));
        Assert.Equal("baseline info", File.ReadAllText(infoPath));
        Assert.False(File.Exists(reviewOnlyPath));
        Assert.True(junction.Active);

        File.WriteAllText(savePath, "saved review selection");
        File.WriteAllText(infoPath, "saved review info");
        File.WriteAllText(reviewOnlyPath, "preserve across restart");
        paths.EnsureDirectories();
        File.WriteAllText(paths.StandardOutputPath, "review stdout");
        File.WriteAllText(paths.TestSaveScenarioLogPath, "review fixture log");

        TestSaveCleanupResult stopped = store.CompleteStopped(
            first,
            "44444444444444444444444444444444");

        Assert.False(junction.Active);
        Assert.True(stopped.ScenarioLogArchived);
        Assert.Contains(
            stopped.ArchivedLogPaths,
            path => path.EndsWith(".review.scenario.log", StringComparison.Ordinal));
        Assert.Equal("saved review selection", File.ReadAllText(savePath));
        Assert.Equal("saved review info", File.ReadAllText(infoPath));
        Assert.Equal("preserve across restart", File.ReadAllText(reviewOnlyPath));
        Assert.Equal(
            "baseline save",
            File.ReadAllText(Path.Combine(paths.TestSaveBaselinePath, create.Identity.SaveId)));

        TestSaveLaunchState resumed = store.PrepareReviewForStart(resetFromBaseline: false)
            .LaunchState;

        Assert.Equal(TestSaveContract.ReviewMode, resumed.Mode);
        Assert.Equal(first.Identity, resumed.Identity);
        Assert.Equal("saved review selection", File.ReadAllText(savePath));
        Assert.Equal("saved review info", File.ReadAllText(infoPath));
        Assert.Equal("preserve across restart", File.ReadAllText(reviewOnlyPath));
        Assert.True(junction.Active);

        store.CompleteStopped(resumed, "55555555555555555555555555555555");

        Assert.False(junction.Active);
        Assert.Equal("saved review selection", File.ReadAllText(savePath));
        Assert.Equal("preserve across restart", File.ReadAllText(reviewOnlyPath));
    }

    [Fact]
    public void ReviewResetRequiresAnInactiveSlotAndRestoresTheExactBaseline()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        WriteCompleteSavePayload(paths, create.Identity, "baseline save", "baseline info");
        store.CompleteStopped(create, LaunchId);
        TestSaveLaunchState review = store.PrepareReviewForStart(resetFromBaseline: true)
            .LaunchState;
        string savePath = Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId);
        string reviewOnlyPath = Path.Combine(paths.TestSaveWorkPath, "review-only");
        File.WriteAllText(savePath, "review mutation");
        File.WriteAllText(reviewOnlyPath, "review mutation");

        Assert.Throws<InvalidOperationException>(store.ResetReview);

        Assert.True(junction.Active);
        Assert.Equal("review mutation", File.ReadAllText(savePath));
        Assert.Equal("review mutation", File.ReadAllText(reviewOnlyPath));

        store.CompleteStopped(review, "66666666666666666666666666666666");
        store.ResetReview();

        Assert.False(junction.Active);
        Assert.Equal("baseline save", File.ReadAllText(savePath));
        Assert.Equal(
            "baseline info",
            File.ReadAllText(Path.Combine(paths.TestSaveWorkPath, "SaveGameInfo")));
        Assert.False(File.Exists(reviewOnlyPath));
        Assert.Empty(Directory.EnumerateDirectories(
            paths.TestSaveRoot,
            ".*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void AbortRemovesOnlyTheExactBindingAndRestoresAnExistingBaseline()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId), "baseline");
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, "SaveGameInfo"), "baseline info");
        store.CompleteStopped(create, LaunchId);
        TestSaveLaunchState scenario = store.PrepareForStart().LaunchState;
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, scenario.Identity.SaveId), "dirty");

        store.AbortStopped(scenario, "55555555555555555555555555555555");

        Assert.False(junction.Active);
        Assert.Equal("baseline", File.ReadAllText(Path.Combine(
            paths.TestSaveWorkPath,
            scenario.Identity.SaveId)));
        Assert.Equal(4, junction.EnsureInactiveCount);
    }

    [Fact]
    public void DriftedWorkMarkerStillUnmountsBeforeBlockingFixtureMutation()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState launch = store.PrepareForStart().LaunchState;
        string markerPath = Path.Combine(
            paths.TestSaveWorkPath,
            TestSaveContract.FixtureMarkerFileName);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(markerPath));
        Dictionary<string, object?> drifted = document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Name == "fixtureId"
                    ? (object?)"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    : JsonSerializer.Deserialize<object>(property.Value.GetRawText()));
        File.WriteAllText(markerPath, JsonSerializer.Serialize(drifted));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => store.AbortStopped(launch, LaunchId));

        Assert.Contains("marker", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(junction.Active);
        Assert.Equal(1, junction.EnsureInactiveCount);
    }

    [Fact]
    public void DriftedManifestStillUnmountsBeforeBlockingFixtureMutation()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState launch = store.PrepareForStart().LaunchState;
        File.WriteAllText(paths.TestSaveManifestPath, "not fixture json");

        Assert.Throws<InvalidDataException>(
            () => store.AbortStopped(launch, LaunchId));

        Assert.False(junction.Active);
        Assert.Equal(1, junction.EnsureInactiveCount);
    }

    [Fact]
    public void InvalidRetainedModeStillUnmountsBeforeFullValidation()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState launch = store.PrepareForStart().LaunchState;
        TestSaveLaunchState invalid = launch with { Mode = "invalid" };

        Assert.Throws<InvalidDataException>(
            () => store.AbortStopped(invalid, LaunchId));

        Assert.False(junction.Active);
        Assert.Equal(1, junction.EnsureInactiveCount);
    }

    [Fact]
    public void FailedInstalledResetRestoresPreviousWorkAndLogArchiveRetryIsIdempotent()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        var injectFailure = false;
        TestSaveFixtureStore store = CreateStore(
            paths,
            saves.Path,
            junction,
            () =>
            {
                if (injectFailure)
                {
                    injectFailure = false;
                    File.WriteAllText(
                        Path.Combine(
                            paths.TestSaveWorkPath,
                            TestSaveContract.FixtureMarkerFileName),
                        "corrupt installed marker");
                }
            });
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        string savePath = Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId);
        File.WriteAllText(savePath, "known baseline");
        File.WriteAllText(Path.Combine(paths.TestSaveWorkPath, "SaveGameInfo"), "known info");
        paths.EnsureDirectories();
        File.WriteAllText(paths.StandardOutputPath, "stable stdout");
        File.WriteAllText(paths.TestSaveScenarioLogPath, "stable scenario log");
        injectFailure = true;

        Assert.Throws<InvalidDataException>(() => store.CompleteStopped(create, LaunchId));

        Assert.False(junction.Active);
        Assert.Equal("known baseline", File.ReadAllText(savePath));

        TestSaveCleanupResult retry = store.CompleteStopped(create, LaunchId);

        Assert.True(retry.ScenarioLogArchived);
        Assert.Equal(2, retry.ArchivedLogPaths.Count);
        Assert.Equal("known baseline", File.ReadAllText(savePath));
        Assert.Empty(Directory.EnumerateDirectories(
            paths.TestSaveRoot,
            ".*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("saveId", "missing")]
    [InlineData("saveId", "empty")]
    [InlineData("saveId", "directory")]
    [InlineData("SaveGameInfo", "missing")]
    [InlineData("SaveGameInfo", "empty")]
    [InlineData("SaveGameInfo", "directory")]
    public void IncompleteStardewPayloadCannotBecomeBaseline(
        string fileName,
        string invalidState)
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        WriteCompleteSavePayload(paths, create.Identity);
        string exactFileName = string.Equals(fileName, "saveId", StringComparison.Ordinal)
            ? create.Identity.SaveId
            : fileName;
        string exactPath = Path.Combine(paths.TestSaveWorkPath, exactFileName);
        switch (invalidState)
        {
            case "missing":
                File.Delete(exactPath);
                break;
            case "empty":
                File.WriteAllText(exactPath, string.Empty);
                break;
            case "directory":
                File.Delete(exactPath);
                Directory.CreateDirectory(exactPath);
                break;
            default:
                throw new InvalidOperationException($"Unknown test state '{invalidState}'.");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => store.CompleteStopped(create, LaunchId));

        Assert.Contains(exactFileName, exception.Message, StringComparison.Ordinal);
        Assert.False(junction.Active);
        Assert.False(Directory.Exists(paths.TestSaveBaselinePath));
    }

    [Fact]
    public void InvalidBaselineIsRejectedBeforeWorkMutationOrMount()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory saves = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        var junction = new FakeJunction();
        TestSaveFixtureStore store = CreateStore(paths, saves.Path, junction);
        TestSaveLaunchState create = store.PrepareForStart().LaunchState;
        WriteCompleteSavePayload(paths, create.Identity, "baseline save", "baseline info");
        store.CompleteStopped(create, LaunchId);
        string workSavePath = Path.Combine(paths.TestSaveWorkPath, create.Identity.SaveId);
        File.WriteAllText(workSavePath, "preserve this work tree");
        File.WriteAllText(
            Path.Combine(paths.TestSaveBaselinePath, "SaveGameInfo"),
            string.Empty);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            store.PrepareForStart);

        Assert.Contains("SaveGameInfo", exception.Message, StringComparison.Ordinal);
        Assert.Equal("preserve this work tree", File.ReadAllText(workSavePath));
        Assert.False(junction.Active);
    }

    [Fact]
    public void IdentityRejectsEveryNonExactStableField()
    {
        TestSaveIdentity identity = Identity();
        TestSaveIdentity[] invalid =
        [
            identity with { SchemaVersion = 2 },
            identity with { WorkspaceOwnerId = "bad" },
            identity with { FixtureId = "bad" },
            identity with { UniqueGameId = 0 },
            identity with { SaveId = "other" },
            identity with { PlayerName = "other" },
            identity with { FarmName = "other" },
            identity with { FavoriteThing = "other" },
        ];

        Assert.All(invalid, value => Assert.Throws<InvalidDataException>(value.Validate));
        identity.Validate();
    }

    private static TestSaveFixtureStore CreateStore(
        LiveLabPaths paths,
        string savesRoot,
        FakeJunction junction,
        Action? afterWorkInstalledForTest = null)
    {
        var ids = new Queue<string>([WorkspaceId, FixtureId]);
        return new TestSaveFixtureStore(
            paths,
            savesRoot,
            junction,
            ids.Dequeue,
            () => 123456789L,
            afterWorkInstalledForTest);
    }

    private static TestSaveIdentity Identity() => new(
        TestSaveContract.SchemaVersion,
        WorkspaceId,
        FixtureId,
        123456789L,
        "SDVKit_123456789",
        TestSaveContract.PlayerName,
        TestSaveContract.FarmName,
        TestSaveContract.FavoriteThing);

    private static TestSaveIdentity ReadIdentity(string path) =>
        JsonSerializer.Deserialize<TestSaveIdentity>(
            File.ReadAllText(path),
            LiveLabJsonOptions.CamelCase)
        ?? throw new InvalidDataException("The test fixture identity was missing.");

    private static void WriteCompleteSavePayload(
        LiveLabPaths paths,
        TestSaveIdentity identity,
        string saveContent = "save",
        string infoContent = "info")
    {
        File.WriteAllText(
            Path.Combine(paths.TestSaveWorkPath, identity.SaveId),
            saveContent);
        File.WriteAllText(
            Path.Combine(paths.TestSaveWorkPath, "SaveGameInfo"),
            infoContent);
    }

    private sealed class FakeJunction : IDirectChildJunction
    {
        public bool Active { get; private set; }

        public int EnsureInactiveCount { get; private set; }

        public int VerifyInactiveCount { get; private set; }

        public string? SavesRoot { get; private set; }

        public string? SlotName { get; private set; }

        public string? TargetPath { get; private set; }

        public void VerifyInactive(string savesRoot, string slotName, string targetPath)
        {
            VerifyInactiveCount++;
            if (Active)
            {
                throw new InvalidOperationException(
                    $"The exact Stardew test-save slot already exists: {Path.Combine(savesRoot, slotName)}");
            }
        }

        public string Activate(string savesRoot, string slotName, string targetPath)
        {
            Assert.False(Active);
            Active = true;
            SavesRoot = savesRoot;
            SlotName = slotName;
            TargetPath = targetPath;
            return Path.Combine(savesRoot, slotName);
        }

        public void VerifyActive(string savesRoot, string slotName, string targetPath)
        {
            Assert.True(Active);
            Assert.Equal(SavesRoot, savesRoot);
            Assert.Equal(SlotName, slotName);
            Assert.Equal(TargetPath, targetPath);
        }

        public void EnsureInactive(string savesRoot, string slotName, string targetPath)
        {
            EnsureInactiveCount++;
            Assert.Equal(SavesRoot, savesRoot);
            Assert.Equal(SlotName, slotName);
            Assert.Equal(TargetPath, targetPath);
            Active = false;
        }
    }
}

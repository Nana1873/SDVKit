using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    private const string RefreshRoot = """{"Format":"2.9.0","Changes":[{"Action":"Include","FromFile":"patches/item.json"}]}""";
    private static string RefreshPatch(string value) => """{"Changes":[{"Action":"EditData","Target":"Data/Objects","Fields":{"388":{"DisplayName":"VALUE"}}}]}""".Replace("VALUE", value, StringComparison.Ordinal);

    private static PreparedReview RefreshReview(TemporaryDirectory temporary, bool variedCase = false)
    {
        var pack = ProjectReviewStagerTests.Artifact(temporary.Path, "Test Pack", ProjectReviewArtifactRole.Target,
            "Test.Pack", contentPackFor: variedCase ? ProjectReviewCpDiagnosis.ProviderId.ToLowerInvariant() : ProjectReviewCpDiagnosis.ProviderId, kind: ProjectInspectionReport.ContentPack);
        File.WriteAllText(Path.Combine(pack.SourceRoot, "content.json"), RefreshRoot);
        Directory.CreateDirectory(Path.Combine(pack.SourceRoot, "patches"));
        File.WriteAllText(Path.Combine(pack.SourceRoot, "patches/item.json"), RefreshPatch("before"));
        var provider = ProjectReviewStagerTests.Artifact(temporary.Path, "ContentPatcher", ProjectReviewArtifactRole.Companion,
            ProjectReviewCpDiagnosis.ProviderId, version: "2.9.1");
        pack = pack with { BuildIdentity = ModBuildIdentity.ComputeFileSet(pack.SourceRoot) };
        var review = PrepareSingle(temporary, [pack, provider], ReadyLoadedMods(
            new LoadedModEntry("Test.Pack", "1.0.0", true), new LoadedModEntry(ProjectReviewCpDiagnosis.ProviderId, "2.9.1", false),
            new LoadedModEntry("SDVKit.AlwaysOn", "0.7.0", false)));
        WriteLog(review.Reader, "");
        File.WriteAllText(Path.Combine(pack.SourceRoot, "patches/item.json"), RefreshPatch("after"));
        return review;
    }

    private static CpRefreshResult Refresh(TemporaryDirectory temporary, PreparedReview review,
        Func<string, LiveLabCommandResult> send, string[]? files = null, Action<string, string>? replace = null,
        string? root = null, string pack = "Test.Pack") =>
        ProjectReviewCpRefresh.Execute(temporary.Path, root ?? review.Staging.Target.SourceRoot, pack,
            ProjectReviewCpDiagnosis.ProviderId, files ?? ["patches/item.json"], "Data/Objects", "388",
            review.ProcessHost, () => ObservedAt.AddSeconds(1), send, replace, TimeSpan.FromMilliseconds(200));

    [Theory]
    [InlineData("success")]
    [InlineData("uncertain")]
    [InlineData("timeout")]
    [InlineData("observationFailure")]
    [InlineData("variedCase")]
    [InlineData("packaged")]
    public void RefreshChangesOnlyOwnedFilesPreservesLaunchAndNeverRetriesReload(string mode)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary, mode == "variedCase");
        if (mode == "packaged")
        {
            Assert.Empty(ProjectPackager.Package(review.Staging.Target.SourceRoot, () => throw new InvalidOperationException()).Problems);
            Assert.Empty(ProjectPackager.Package(review.Staging.Target.SourceRoot, () => throw new InvalidOperationException()).Problems);
        }
        var paths = LiveLabPaths.Resolve(temporary.Path);
        var stateBefore = new JsonLiveLabStateStore(paths.StatePath).Read();
        string sourceBefore = ModBuildIdentity.ComputeFileSet(review.Staging.Target.SourceRoot);
        string providerBefore = ModBuildIdentity.ComputeFileSet(review.Staging.Artifacts[1].StagingPath);
        string log = Path.Combine(paths.StardewDataPath, "ErrorLogs", "SMAPI-latest.txt");
        int reloads = 0;
        string valueInRuntime = "before";
        LiveLabCommandResult Send(string command)
        {
            Assert.Null(LiveLabOperationLock.TryAcquire(temporary.Path));
            Assert.Null(ProjectReviewActionLock.TryAcquire(paths.RuntimePath));
            var current = ProjectModStager.ReadReview(paths);
            Assert.Null(current.Problem);
            Assert.True(current.Staging!.Target.CpRefresh!.RequiresRestart);
            string message;
            if (command.StartsWith("patch reload", StringComparison.Ordinal))
            {
                Assert.Equal("patch reload \"Test.Pack\"", command);
                reloads++;
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(review.Staging.Target.StagingPath, "patches/item.json")));
                valueInRuntime = json.RootElement.GetProperty("Changes")[0].GetProperty("Fields").GetProperty("388").GetProperty("DisplayName").GetString()!;
                if (mode is "uncertain" or "timeout") return new(mode == "uncertain" ? 3 : 0,
                    new ProjectReviewCommandReport(1, null, temporary.Path, "running", null, mode == "uncertain" ? null : true, [], []));
                message = "[08:00:02 TRACE ContentPatcher] Requested cache invalidation for all assets matching a predicate.\n"
                    + "[08:00:02 TRACE SMAPI] Invalidated 1 asset names (Data/Objects).\nPropagated 1 core assets (Data/Objects).\n"
                    + "[08:00:02 INFO  ContentPatcher] Content pack reloaded.\n";
            }
            else if (command.StartsWith("patch summary", StringComparison.Ordinal))
                message = CpSummary.Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal).Replace("Patcher]\n", "Patcher] \n", StringComparison.Ordinal) + "\n";
            else if (command.StartsWith("patch parse", StringComparison.Ordinal))
                message = CpMarker(command.Split('"')[1]).Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal);
            else
            {
                Assert.StartsWith("sdvkit data ", command);
                if (mode == "observationFailure") return new(3, new ProjectReviewCommandReport(1, null, temporary.Path, "blocked", null, false, [], []));
                string id = command.Split(' ')[2];
                var report = new ReviewDataReport(1, "ready", "get", "test", "test", "Data/Objects", "object", "dictionary", "string", "388",
                    null, null, null, null, JsonSerializer.SerializeToElement(new { DisplayName = valueInRuntime }), []);
                File.WriteAllText(ReviewDataContract.ResponsePath(paths.RuntimePath, id), JsonSerializer.Serialize(new ReviewDataResponseEnvelope(1, id, report)));
                message = "";
            }
            File.AppendAllText(log, message);
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "running", null, true, [], []));
        }
        var result = mode == "variedCase" ? ProjectReviewCpRefresh.Execute(temporary.Path, review.Staging.Target.SourceRoot, "test.pack",
            "pathoschild.contentpatcher", ["patches/item.json"], "Data/Objects", "388", review.ProcessHost, () => ObservedAt.AddSeconds(1), Send)
            : Refresh(temporary, review, Send);
        bool succeeded = mode is "success" or "variedCase" or "packaged";
        Assert.Equal(succeeded ? "observed" : "incomplete", result.State);
        Assert.Equal(1, reloads);
        Assert.Equal("after", valueInRuntime);
        Assert.Equal(stateBefore, new JsonLiveLabStateStore(paths.StatePath).Read());
        Assert.Equal(sourceBefore, ModBuildIdentity.ComputeFileSet(review.Staging.Target.SourceRoot));
        Assert.Equal(providerBefore, ModBuildIdentity.ComputeFileSet(review.Staging.Artifacts[1].StagingPath));
        Assert.Equal(review.Staging.Target.BuildIdentity, result.LaunchBuildIdentity);
        Assert.NotEqual(result.LaunchBuildIdentity, result.Refresh!.StagedBuildIdentity);
        Assert.True(review.Reader.ReadContext().Succeeded);
        Assert.Null(ProjectModStager.ReadReview(paths).Problem);
        Assert.Equal(!succeeded, result.Refresh.RequiresRestart);
        if (succeeded) Assert.Equal("after", Assert.IsType<ReviewDataReport>(result.Observation).Record!.Value.GetProperty("DisplayName").GetString());
        else Assert.Equal("cpRefreshRestartRequired", Refresh(temporary, review, Send).ErrorCode);
        Assert.Equal(1, reloads);
        // Same marker-selected cleanup, even with uncertain runtime delivery.
        Assert.Null(ProjectModStager.ReadReviewForCleanup(paths, "single").Problem);
        Assert.True(ProjectModStager.RemoveReview(paths).Removed);
        Assert.False(Directory.Exists(review.Staging.Target.StagingPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshPartialCopyRestoresOrRetainsVisibleRecoveryWithoutDispatch(bool failRollback)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.SourceRoot, "content.json"), RefreshRoot.Replace("\"Action\":", "\"LogName\":\"Included\",\"Action\":", StringComparison.Ordinal));
        int calls = 0;
        void Replace(string source, string destination)
        {
            calls++;
            if (calls == 2 || failRollback && calls >= 3) throw new IOException("Injected copy failure");
            File.Copy(source, destination, true);
        }
        var result = Refresh(temporary, review, _ => throw new InvalidOperationException("No reload before complete copies"),
            ["content.json", "patches/item.json"], Replace);
        Assert.Equal("incomplete", result.State);
        Assert.Equal(!failRollback, result.StagingRestored);
        Assert.Equal(failRollback ? "cpRefreshRollbackIncomplete" : "cpRefreshCopyFailedRestored", result.ErrorCode);
        Assert.False(result.Refresh!.CommandWritten);
        Assert.True(result.Refresh.RequiresRestart);
        var paths = LiveLabPaths.Resolve(temporary.Path);
        if (failRollback) Assert.NotNull(ProjectModStager.ReadReview(paths).Problem);
        else Assert.Equal(review.Staging.Target.BuildIdentity, ModBuildIdentity.ComputeFileSet(review.Staging.Target.StagingPath));
        Assert.Null(ProjectModStager.ReadReviewForCleanup(paths, "single").Problem);
        Assert.True(ProjectModStager.RemoveReview(paths).Removed);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("manifest")]
    [InlineData("provider")]
    [InlineData("asset")]
    [InlineData("configSchema")]
    [InlineData("dynamicTokens")]
    [InlineData("notIncluded")]
    [InlineData("large")]
    [InlineData("outside")]
    [InlineData("pack")]
    [InlineData("drift")]
    [InlineData("code")]
    [InlineData("stagedOutput")]
    [InlineData("providerOutput")]
    [InlineData("nestedOutput")]
    [InlineData("packagedUnselected")]
    public void RefreshRejectsUnsupportedOrMismatchedChangesBeforeMutation(string change)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        string root = review.Staging.Target.SourceRoot;
        string[] files = ["patches/item.json"];
        switch (change)
        {
            case "stagedOutput": temporary.WriteFile(Path.GetRelativePath(temporary.Path, review.Staging.Target.StagingPath) + "/.sdvkit/payload.json", "changed"); break;
            case "providerOutput": temporary.WriteFile(Path.GetRelativePath(temporary.Path, review.Staging.Artifacts[1].SourceRoot) + "/.sdvkit/payload.json", "changed"); break;
            case "nestedOutput": temporary.WriteFile(Path.GetRelativePath(temporary.Path, root) + "/patches/.sdvkit/payload.json", "changed"); break;
            case "packagedUnselected":
                Assert.Empty(ProjectPackager.Package(root, () => throw new InvalidOperationException()).Problems);
                File.WriteAllText(Path.Combine(root, "unselected.json"), "{}");
                break;
            case "invalid": File.WriteAllText(Path.Combine(root, files[0]), "{ invalid"); break;
            case "manifest": File.AppendAllText(Path.Combine(root, "manifest.json"), " "); break;
            case "provider": File.AppendAllText(Path.Combine(review.Staging.Artifacts[1].SourceRoot, "ContentPatcher.dll"), "changed"); break;
            case "asset": File.WriteAllText(Path.Combine(root, "image.png"), "changed"); break;
            case "code": File.WriteAllText(Path.Combine(root, "extra.dll"), "changed"); break;
            case "configSchema":
            case "dynamicTokens":
                files = ["content.json", "patches/item.json"];
                File.WriteAllText(Path.Combine(root, "content.json"), RefreshRoot.Replace("\"Format\"", change == "configSchema" ? "\"ConfigSchema\":{},\"Format\"" : "\"DynamicTokens\":[],\"Format\"", StringComparison.Ordinal));
                break;
            case "notIncluded": files = ["interiors.json"]; break;
            case "large": File.WriteAllText(Path.Combine(root, files[0]), new string(' ', ProjectReviewCpRefresh.MaximumFileBytes + 1)); break;
            case "drift": File.AppendAllText(Path.Combine(review.Staging.Target.StagingPath, "content.json"), " "); break;
        }
        string stagedBefore = ModBuildIdentity.ComputeFileSet(review.Staging.Target.StagingPath);
        string sourceBefore = ModBuildIdentity.ComputeFileSet(root);
        var result = Refresh(temporary, review, _ => throw new InvalidOperationException("Rejected input must not dispatch"), files,
            root: change == "outside" ? temporary.Path : null, pack: change == "pack" ? "Other.Pack" : "Test.Pack");
        Assert.Equal("rejected", result.State);
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(0, result.FilesReplaced);
        Assert.Equal(stagedBefore, ModBuildIdentity.ComputeFileSet(review.Staging.Target.StagingPath));
        Assert.Equal(sourceBefore, ModBuildIdentity.ComputeFileSet(root));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("C:/outside.json")]
    [InlineData("patches\\item.json")]
    [InlineData("manifest.json")]
    [InlineData("config.json")]
    [InlineData("i18n/default.json")]
    [InlineData("patches/../item.json")]
    [InlineData("patches/item.json:stream.json")]
    public void RefreshPathArgumentsFailClosed(string file) => Assert.False(ProjectReviewCpRefresh.ValidFiles([file]));

    [Fact]
    public void RefreshSelectionAndBothExistingLocksAreBounded()
    {
        Assert.False(ProjectReviewCpRefresh.ValidFiles([]));
        Assert.False(ProjectReviewCpRefresh.ValidFiles(["a.json", "A.json"]));
        Assert.False(ProjectReviewCpRefresh.ValidFiles(Enumerable.Range(0, 17).Select(i => i + ".json").ToArray()));
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var paths = LiveLabPaths.Resolve(temporary.Path);
        using (var held = LiveLabOperationLock.TryAcquire(temporary.Path))
            Assert.Equal("reviewBusy", Refresh(temporary, review, _ => throw new InvalidOperationException()).ErrorCode);
        using (var held = ProjectReviewActionLock.TryAcquire(paths.RuntimePath))
            Assert.Equal("reviewBusy", Refresh(temporary, review, _ => throw new InvalidOperationException()).ErrorCode);
    }

    [Fact]
    public void LogReadRejectsAChangedAuthorizedGenerationWithTheSameLaunch()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var before = review.Reader.ReadContext().Context!;
        var target = before.Staging.Target;
        var changed = before.Staging with
        {
            Artifacts = before.Staging.Artifacts.Select(a => a == target
            ? a with { CpRefresh = new(Guid.NewGuid().ToString("N"), LaunchId, a.BuildIdentity, a.BuildIdentity, ["content.json"], true, false) } : a).ToArray()
        };
        ProjectModStager.WriteReviewOwnership(changed.OwnershipPath, changed, replace: true);
        Assert.Equal(before.State, review.Reader.ReadContext().Context!.State);
        Assert.Throws<InvalidDataException>(() => OwnedReviewLogReader.Read(review.Reader, before));
    }

    [Theory]
    [InlineData("sourceHardLink")]
    [InlineData("sourceJunction")]
    [InlineData("stagedHardLink")]
    [InlineData("preparationLink")]
    [InlineData("outputHardLink")]
    [InlineData("outputJunction")]
    public void RefreshRejectsLinksWithoutChangingTheLinkedFile(string kind)
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        string source = Path.Combine(review.Staging.Target.SourceRoot, "patches/item.json");
        string staged = Path.Combine(review.Staging.Target.StagingPath, "patches/item.json");
        string foreign = Path.Combine(temporary.Path, "foreign.json");
        File.WriteAllText(foreign, RefreshPatch("protected"));
        if (kind is "outputHardLink" or "outputJunction")
        {
            string output = Path.Combine(review.Staging.Target.SourceRoot, ".sdvkit");
            if (kind == "outputJunction") new Win32DirectChildJunctionPlatform().CreateDirectoryJunction(output, temporary.Path);
            else
            {
                Directory.CreateDirectory(output);
                Assert.True(CreateHardLink(Path.Combine(output, "linked.json"), foreign, IntPtr.Zero));
            }
            Assert.Throws<InvalidDataException>(() => ProjectModStager.ComputeCpSourceIdentity(review.Staging.Target.SourceRoot));
        }
        if (kind == "sourceHardLink") Assert.True(CreateHardLink(source + ".link", source, IntPtr.Zero));
        if (kind == "stagedHardLink") Assert.True(CreateHardLink(staged + ".link", staged, IntPtr.Zero));
        if (kind == "sourceJunction")
        {
            File.Delete(source);
            Directory.Delete(Path.GetDirectoryName(source)!);
            new Win32DirectChildJunctionPlatform().CreateDirectoryJunction(Path.GetDirectoryName(source)!, temporary.Path);
        }
        if (kind == "preparationLink") new Win32DirectChildJunctionPlatform().CreateDirectoryJunction(Path.Combine(LiveLabPaths.Resolve(temporary.Path).SingleRoot, "review-prepared"), temporary.Path);
        try
        {
            var result = Refresh(temporary, review, _ => throw new InvalidOperationException("Linked paths must not dispatch"));
            Assert.Equal("rejected", result.State);
            Assert.Equal(0, result.FilesReplaced);
            Assert.Equal(RefreshPatch("protected"), File.ReadAllText(foreign));
        }
        finally
        {
            if (kind == "outputJunction") new Win32DirectChildJunctionPlatform().DeleteExactDirectoryJunction(Path.Combine(review.Staging.Target.SourceRoot, ".sdvkit"), temporary.Path);
            if (kind == "preparationLink") new Win32DirectChildJunctionPlatform().DeleteExactDirectoryJunction(Path.Combine(temporary.Path, ".sdvkit/lab/single/review-prepared"), temporary.Path);
            if (kind == "sourceJunction") new Win32DirectChildJunctionPlatform().DeleteExactDirectoryJunction(Path.GetDirectoryName(source)!, temporary.Path);
        }
    }

    [Fact]
    public void RefreshCountsExcludedPackageOutputTowardSourceSizeLimitBeforeMutation()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        string output = Path.Combine(review.Staging.Target.SourceRoot, ".sdvkit", "packages");
        Directory.CreateDirectory(output);
        string archive = Path.Combine(output, "large.zip");
        const long length = 256L * 1024 * 1024 + 1;
        using (var stream = File.Create(archive)) stream.SetLength(length);
        string stagedBefore = ModBuildIdentity.ComputeFileSet(review.Staging.Target.StagingPath);
        var result = Refresh(temporary, review, _ => throw new InvalidOperationException("Oversized source must not dispatch"));
        Assert.Equal("rejected", result.State);
        Assert.Equal("cpRefreshPackTooLarge", result.ErrorCode);
        Assert.Equal(0, result.FilesReplaced);
        Assert.Null(result.Refresh);
        Assert.Equal(stagedBefore, ModBuildIdentity.ComputeFileSet(review.Staging.Target.StagingPath));
        Assert.Equal(length, new FileInfo(archive).Length);
    }

    [Fact]
    public void RefreshSelectedIncludeUsesTheActualSchemaAndRejectsUnreachableJson()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        string root = review.Staging.Target.SourceRoot;
        File.WriteAllText(Path.Combine(root, "patches/item.json"), """{"Changes":[{"Action":"InventedAction","Target":"Data/Objects"}]}""");
        var invalid = Refresh(temporary, review, _ => throw new InvalidOperationException());
        Assert.StartsWith("cpRefreshInvalidPatch:", invalid.ErrorCode);
        File.WriteAllText(Path.Combine(root, "interiors.json"), RefreshPatch("orphan"));
        var orphan = Refresh(temporary, review, _ => throw new InvalidOperationException(), ["interiors.json"]);
        Assert.Equal("cpRefreshFileNotIncluded", orphan.ErrorCode);
    }

    [Fact]
    public void RefreshNeverAcceptsAReceiptForNetworkOrAnotherLaunch()
    {
        using TemporaryDirectory temporary = new();
        var review = RefreshReview(temporary);
        var target = review.Staging.Target;
        var changed = review.Staging with
        {
            Artifacts = review.Staging.Artifacts.Select(a => a == target
            ? a with { CpRefresh = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), a.BuildIdentity, a.BuildIdentity, ["content.json"], true, false) } : a).ToArray()
        };
        ProjectModStager.WriteReviewOwnership(changed.OwnershipPath, changed, replace: true);
        Assert.Equal("reviewOwnershipMismatch", review.Reader.ReadContext().ErrorCode);
        using TemporaryDirectory networkRoot = new();
        var paths = LiveLabPaths.Resolve(networkRoot.Path);
        var staged = ProjectModStager.StageReview(CompleteArtifacts(networkRoot.Path), NetworkTwoContract.Topology, paths).Staging!;
        var network = staged with { Artifacts = staged.Artifacts.Select(a => a == staged.Target ? a with { CpRefresh = changed.Target.CpRefresh } : a).ToArray() };
        ProjectModStager.WriteReviewOwnership(network.OwnershipPath, network, replace: true);
        Assert.NotNull(ProjectModStager.ReadReview(paths, NetworkTwoContract.Topology).Problem);
    }

    [Fact]
    public void RefreshCliRequiresExplicitObservationAndRejectsDuplicateScope()
    {
        string[] args = ["project", "review", "cp-refresh", "pack", "--pack", "Test.Pack", "--provider", ProjectReviewCpDiagnosis.ProviderId,
            "--file", "content.json", "--observe-data", "Data/Objects", "--key", "388", "--json"];
        Assert.True(CliApplication.TryParseCpRefresh(args, out _, out _, out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpRefresh(args.Concat(["--pack", "Other.Pack"]).ToArray(), out _, out _, out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpRefresh(args.Concat(["--file", "content.json"]).ToArray(), out _, out _, out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpRefresh(args.Where(s => s is not ("--key" or "388")).ToArray(), out _, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("normal", "ready")]
    [InlineData("error", "incomplete")]
    [InlineData("duplicateTrace", "incomplete")]
    [InlineData("unknownTrace", "incomplete")]
    [InlineData("interruptedReply", "incomplete")]
    [InlineData("missingReply", "incomplete")]
    public void ReloadRecognizesOnlyTheKnownInvalidationPreludeAndCompleteReply(string mode, string expected)
    {
        string trace = "[08:00:02 TRACE Content Patcher] Requested cache invalidation for all assets matching a predicate.\n";
        string foreign = "[08:00:02 TRACE SMAPI] Invalidated 1 asset names (Data/Objects).\nPropagated 1 core assets (Data/Objects).\n";
        string reply = "[08:00:02 INFO  Content Patcher] Content pack reloaded.\n";
        string middle = trace + foreign + reply;
        if (mode == "error") middle = trace + "[08:00:02 ERROR Content Patcher] Invalid patch\n" + reply;
        if (mode == "duplicateTrace") middle = trace + trace + reply;
        if (mode == "unknownTrace") middle = trace.Replace("predicate", "unknown", StringComparison.Ordinal) + reply;
        if (mode == "interruptedReply") middle += foreign;
        if (mode == "missingReply") middle = trace + foreign;
        var result = ProjectReviewCpDiagnosis.InterpretWindow(CpMarker("begin") + middle + CpMarker("end"),
            "Content Patcher", "Test.Pack", null, null, "begin", "end", false, [], DateTimeOffset.UtcNow, isReload: true);
        Assert.Equal(expected, result.State);
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.AlwaysOn;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class ProjectReviewScreenshotServiceTests
{
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Theory]
    [InlineData("map")]
    [InlineData("viewport")]
    public void BuildCommandBindsTheClosedModeAndSafeLabel(string mode)
    {
        string requestId = Guid.NewGuid().ToString("N");

        string command = ProjectReviewScreenshotService.BuildCommand(
            requestId,
            new ReviewScreenshotCaptureQuery(mode, "proof_1"));

        Assert.Equal(
            $"sdvkit screenshot capture {requestId} {mode} proof_1",
            command);
        Assert.Equal(
            string.Equals(mode, "viewport", StringComparison.Ordinal),
            ProjectReviewConsoleLine.CanRunBeforeScenarioReady(command));
    }

    [Theory]
    [InlineData("desktop", "proof", "screenshotModeInvalid")]
    [InlineData("map", "../escape", "screenshotLabelInvalid")]
    [InlineData("map", "Mäp", "screenshotLabelInvalid")]
    public void InvalidQueryFailsBeforeResolvingOrSending(
        string mode,
        string label,
        string expectedCode)
    {
        ProjectReviewScreenshotResult result = ProjectReviewScreenshotService.Execute(
            new ReviewScreenshotCaptureQuery(mode, label),
            LiveLabState.SingleTopology,
            role: null,
            "not-used");

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, Assert.Single(result.Problems).Code);
    }

    [Theory]
    [InlineData("single", null)]
    [InlineData("network-2", "host")]
    [InlineData("network-2", "farmhand")]
    public void ExactRoleDestinationRefusesOverwriteBeforeReviewTransport(
        string topology,
        string? role)
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = ProjectReviewScreenshotService.ResolveRolePaths(
            temporary.Path,
            topology,
            role);
        paths.EnsureDirectories();
        string target = ProjectReviewScreenshotService.ExpectedPngPath(
            paths,
            ReviewScreenshotContract.FileName("existing"));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, PngTestData.CreateRgba8(1, 1));
        var sender = new ProjectReviewServiceTests.RecordingConsoleInputSender(
            new ProjectReviewConsoleInputResult(
                ProjectReviewConsoleInputStatus.Written));

        ProjectReviewScreenshotResult result = ProjectReviewScreenshotService.Execute(
            new ReviewScreenshotCaptureQuery("viewport", "existing"),
            topology,
            role,
            temporary.Path,
            sender);

        Assert.False(result.Succeeded);
        Assert.Equal("screenshotAlreadyExists", Assert.Single(result.Problems).Code);
        Assert.Equal(0, sender.CallCount);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ScreenshotPathValidationAllowsTheIsolatedTestSaveLinkSibling()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string ownedWorkCopy = Path.Combine(temporary.Path, "owned-work-copy");
        Directory.CreateDirectory(ownedWorkCopy);
        string mountedSave = Path.Combine(paths.SavesPath, "SDVKit_TestSave");
        try
        {
            Directory.CreateSymbolicLink(mountedSave, ownedWorkCopy);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        LiveLabPaths resolved = ProjectReviewScreenshotService.ResolveRolePaths(
            temporary.Path,
            LiveLabState.SingleTopology,
            role: null);
        string expectedPath = ProjectReviewScreenshotService.ExpectedPngPath(
            resolved,
            ReviewScreenshotContract.FileName("with_test_save"));

        ProjectReviewScreenshotService.ValidateScreenshotPath(resolved, expectedPath);
    }

    [Fact]
    public void ResponseRequiresExactRecursiveShapeIdentityAndFreshness()
    {
        const string requestId = "0123456789abcdef0123456789abcdef";
        var query = new ReviewScreenshotCaptureQuery("viewport", "proof");
        DateTimeOffset requestedAt = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        ReviewScreenshotResponseEnvelope valid = ReadyEnvelope(
            requestId,
            query,
            requestedAt.AddMilliseconds(1));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(valid, WireJsonOptions);

        ReviewScreenshotResponseEnvelope parsed = Assert.IsType<ReviewScreenshotResponseEnvelope>(
            ProjectReviewScreenshotService.DeserializeResponse(bytes));
        Assert.True(ProjectReviewScreenshotService.MatchesResponse(
            parsed,
            requestId,
            query,
            requestedAt));
        Assert.False(ProjectReviewScreenshotService.MatchesResponse(
            valid with
            {
                Report = valid.Report with { FileName = "../outside.png" },
            },
            requestId,
            query,
            requestedAt));
        Assert.False(ProjectReviewScreenshotService.MatchesResponse(
            valid with
            {
                Report = valid.Report with { CapturedAtUtc = requestedAt.AddTicks(-1) },
            },
            requestId,
            query,
            requestedAt));
        Assert.False(ProjectReviewScreenshotService.MatchesResponse(
            valid with { RequestId = Guid.NewGuid().ToString("N") },
            requestId,
            query,
            requestedAt));

        JsonObject unknownMember = JsonNode.Parse(bytes)!.AsObject();
        unknownMember["report"]!["path"] = "C:\\outside.png";
        Assert.Throws<InvalidDataException>(() =>
            ProjectReviewScreenshotService.DeserializeResponse(
                Encoding.UTF8.GetBytes(unknownMember.ToJsonString())));
    }

    [Theory]
    [InlineData("map")]
    [InlineData("viewport")]
    public void ReadyReviewReturnsOnlyTheFreshExactPngAndLeavesItOwned(string mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = ProjectReviewServiceTests.StageTarget(
            paths,
            temporary.Path);
        (OwnedProcessIdentity identity, Process child) =
            ProjectReviewServiceTests.StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ProjectReviewServiceTests.ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                ProjectReviewServiceTests.WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string? responsePath = null;
                string? screenshotPath = null;
                byte[] expectedBytes = string.Equals(
                    mode,
                    ReviewScreenshotContract.MapMode,
                    StringComparison.Ordinal)
                        ? PngTestData.CreateRgb8(
                            2,
                            1,
                            [255, 0, 0, 0, 255, 0])
                        : PngTestData.CreateRgba8(
                            2,
                            1,
                            [255, 0, 0, 255, 0, 255, 0, 255]);
                var sender = new ProjectReviewServiceTests.RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written),
                    line =>
                    {
                        string[] tokens = line.Split(' ');
                        string requestId = tokens[3];
                        var query = new ReviewScreenshotCaptureQuery(tokens[4], tokens[5]);
                        screenshotPath = ProjectReviewScreenshotService.ExpectedPngPath(
                            paths,
                            ReviewScreenshotContract.FileName(query.Label));
                        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                        File.WriteAllBytes(screenshotPath, expectedBytes);
                        File.SetLastWriteTimeUtc(screenshotPath, now.UtcDateTime);
                        ReviewScreenshotResponseFile.Write(
                            paths.RuntimePath,
                            ReviewScreenshotResponse.Create(
                                requestId,
                                query,
                                new ReviewScreenshotResult(
                                    true,
                                    "Created.",
                                    FileName: ReviewScreenshotContract.FileName(query.Label)),
                                now));
                        responsePath = ReviewScreenshotContract.ResponsePath(
                            paths.RuntimePath,
                            requestId);
                    });

                ProjectReviewScreenshotResult result = ProjectReviewScreenshotService.Execute(
                    new ReviewScreenshotCaptureQuery(mode, $"{mode}_proof"),
                    LiveLabState.SingleTopology,
                    role: null,
                    temporary.Path,
                    sender,
                    utcNow: () => now);

                ProjectReviewScreenshotCapture capture =
                    Assert.IsType<ProjectReviewScreenshotCapture>(result.Capture);
                Assert.Empty(result.Problems);
                Assert.Equal(expectedBytes, capture.PngBytes);
                Assert.Equal(2, capture.Width);
                Assert.Equal(1, capture.Height);
                Assert.Equal(expectedBytes.Length, capture.EncodedBytes);
                Assert.Matches("^sha256:[0-9a-f]{64}$", capture.Sha256);
                Assert.Equal(1, sender.CallCount);
                Assert.StartsWith(
                    "sdvkit screenshot capture ",
                    sender.Line,
                    StringComparison.Ordinal);
                Assert.NotNull(screenshotPath);
                Assert.True(File.Exists(screenshotPath));
                Assert.NotNull(responsePath);
                Assert.False(File.Exists(responsePath));
            }
            finally
            {
                ProjectReviewServiceTests.EnsureExited(child);
            }
        }
    }

    [Fact]
    public void ReadyReviewTimesOutWithoutRetrying()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = ProjectReviewServiceTests.StageTarget(
            paths,
            temporary.Path);
        (OwnedProcessIdentity identity, Process child) =
            ProjectReviewServiceTests.StartRunningProcess(temporary.Path);
        using (child)
        {
            try
            {
                LiveLabState state = ProjectReviewServiceTests.ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                ProjectReviewServiceTests.WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState);
                var sender = new ProjectReviewServiceTests.RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));

                ProjectReviewScreenshotResult result = ProjectReviewScreenshotService.Execute(
                    new ReviewScreenshotCaptureQuery("viewport", "timeout"),
                    LiveLabState.SingleTopology,
                    role: null,
                    temporary.Path,
                    sender,
                    responseTimeout: TimeSpan.Zero);

                Assert.False(result.Succeeded);
                Assert.Equal("screenshotResponseTimedOut", Assert.Single(result.Problems).Code);
                Assert.Equal(1, sender.CallCount);
            }
            finally
            {
                ProjectReviewServiceTests.EnsureExited(child);
            }
        }
    }

    [Fact]
    public void CancellationPropagatesBeforeAnyPathOrTransportAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ProjectReviewScreenshotService.Execute(
                new ReviewScreenshotCaptureQuery("viewport", "cancelled"),
                LiveLabState.SingleTopology,
                role: null,
                "not-used",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void CancellationStopsTheResponseWaitWithoutRetrying()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewStaging staging = ProjectReviewServiceTests.StageTarget(
            paths,
            temporary.Path);
        (OwnedProcessIdentity identity, Process child) =
            ProjectReviewServiceTests.StartRunningProcess(temporary.Path);
        using (child)
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                LiveLabState state = ProjectReviewServiceTests.ReviewState(
                    paths,
                    staging.TargetLaunchState,
                    identity);
                new JsonLiveLabStateStore(paths.StatePath).Write(state);
                ProjectReviewServiceTests.WriteLoadedStatus(
                    paths,
                    state,
                    staging.TargetLaunchState);
                var sender = new ProjectReviewServiceTests.RecordingConsoleInputSender(
                    new ProjectReviewConsoleInputResult(
                        ProjectReviewConsoleInputStatus.Written));
                var waits = 0;

                Assert.Throws<OperationCanceledException>(() =>
                    ProjectReviewScreenshotService.Execute(
                        new ReviewScreenshotCaptureQuery("viewport", "cancel_wait"),
                        LiveLabState.SingleTopology,
                        role: null,
                        temporary.Path,
                        sender,
                        delay: _ =>
                        {
                            waits++;
                            cancellation.Cancel();
                        },
                        responseTimeout: TimeSpan.FromSeconds(5),
                        cancellationToken: cancellation.Token));
                Assert.Equal(1, sender.CallCount);
                Assert.Equal(1, waits);
            }
            finally
            {
                ProjectReviewServiceTests.EnsureExited(child);
            }
        }
    }

    [Fact]
    public void ScreenshotValidationAcceptsMapRgb8WithoutWideningTextureRgba8()
    {
        byte[] pixels = [255, 0, 0, 0, 255, 0];
        byte[] png = PngTestData.CreateRgb8(2, 1, pixels);
        using var screenshotStream = new MemoryStream(png);

        Assert.True(ReviewTexturePngValidator.TryValidateRgbOrRgba8(
            screenshotStream,
            ReviewScreenshotContract.MaximumPngBytes,
            ReviewScreenshotContract.MaximumDimension,
            ReviewScreenshotContract.MaximumPixels,
            out ReviewTexturePngInfo? info));
        Assert.Equal(2, Assert.IsType<ReviewTexturePngInfo>(info).Width);

        using var textureStream = new MemoryStream(png);
        Assert.False(ReviewTexturePngValidator.TryValidateRgba8(
            textureStream,
            ReviewScreenshotContract.MaximumPngBytes,
            ReviewScreenshotContract.MaximumDimension,
            ReviewScreenshotContract.MaximumPixels,
            out _));
    }

    [Fact]
    public void PngValidationRejectsStaleInvalidOversizedAndReparseEvidence()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        var query = new ReviewScreenshotCaptureQuery("viewport", "proof");
        string fileName = ReviewScreenshotContract.FileName(query.Label);
        string path = ProjectReviewScreenshotService.ExpectedPngPath(paths, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DateTimeOffset requestAt = DateTimeOffset.UtcNow;
        File.WriteAllBytes(path, PngTestData.CreateRgba8(2, 1));
        File.SetLastWriteTimeUtc(path, requestAt.UtcDateTime);

        Assert.NotNull(ProjectReviewScreenshotService.ReadAndValidatePng(
            path,
            query,
            fileName,
            requestAt,
            requestAt,
            requestAt,
            CancellationToken.None));

        File.SetLastWriteTimeUtc(path, requestAt.AddMinutes(-1).UtcDateTime);
        Assert.Null(ProjectReviewScreenshotService.ReadAndValidatePng(
            path,
            query,
            fileName,
            requestAt,
            requestAt,
            requestAt,
            CancellationToken.None));

        File.WriteAllText(path, "not a png");
        File.SetLastWriteTimeUtc(path, requestAt.UtcDateTime);
        Assert.Null(ProjectReviewScreenshotService.ReadAndValidatePng(
            path,
            query,
            fileName,
            requestAt,
            requestAt,
            requestAt,
            CancellationToken.None));

        using (FileStream stream = File.Open(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(ReviewScreenshotContract.MaximumPngBytes + 1L);
        }
        File.SetLastWriteTimeUtc(path, requestAt.UtcDateTime);
        Assert.Null(ProjectReviewScreenshotService.ReadAndValidatePng(
            path,
            query,
            fileName,
            requestAt,
            requestAt,
            requestAt,
            CancellationToken.None));

        File.Delete(path);
        string outside = temporary.WriteFile("outside.png", "not a png");
        try
        {
            File.CreateSymbolicLink(path, outside);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() =>
            ProjectReviewScreenshotService.ValidateScreenshotPath(paths, path));
        Assert.Equal("not a png", File.ReadAllText(outside));
    }

    private static ReviewScreenshotResponseEnvelope ReadyEnvelope(
        string requestId,
        ReviewScreenshotCaptureQuery query,
        DateTimeOffset capturedAtUtc) => new(
            ReviewScreenshotContract.SchemaVersion,
            requestId,
            new ReviewScreenshotReport(
                ReviewScreenshotContract.SchemaVersion,
                "ready",
                query.Mode,
                query.Label,
                ReviewScreenshotContract.FileName(query.Label),
                capturedAtUtc,
                []));
}

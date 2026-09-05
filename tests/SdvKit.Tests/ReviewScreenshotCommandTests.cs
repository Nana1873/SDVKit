using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewScreenshotCommandTests
{
    [Fact]
    public void ParserAcceptsOnlyTheExactScreenshotActionAndArity()
    {
        Assert.True(
            ReviewScreenshotArguments.TryParse(
                ["screenshot", "Vanilla_1"],
                out ReviewScreenshotRequest? request,
                out string acceptedError),
            acceptedError);
        Assert.Equal(ReviewScreenshotKind.Map, request!.Kind);
        Assert.Equal("Vanilla_1", request.Label);

        Assert.True(
            ReviewScreenshotArguments.TryParse(
                ["screenshot", "viewport", "Menu_1"],
                out request,
                out acceptedError),
            acceptedError);
        Assert.Equal(ReviewScreenshotKind.Viewport, request!.Kind);
        Assert.Equal("Menu_1", request.Label);

        Assert.False(ReviewScreenshotArguments.TryParse([], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["status"], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["Screenshot", "x"], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["screenshot"], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["screenshot", "Viewport", "x"], out _, out _));
        Assert.False(
            ReviewScreenshotArguments.TryParse(
                ["screenshot", "x", "extra"],
                out _,
                out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-")]
    [InlineData("Map-Review_01")]
    public void LabelValidationAcceptsOnlyValidAsciiBoundaries(string label)
    {
        Assert.True(ReviewScreenshotArguments.IsValidLabel(label));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("map.png")]
    [InlineData("folder/map")]
    [InlineData("folder\\map")]
    [InlineData("Mäp")]
    public void LabelValidationRejectsUnsafeOrNonAsciiValues(string label)
    {
        Assert.False(ReviewScreenshotArguments.IsValidLabel(label));
    }

    [Fact]
    public void LabelValidationRejectsMoreThan64Characters()
    {
        Assert.False(ReviewScreenshotArguments.IsValidLabel(new string('a', 65)));
    }

    [Theory]
    [InlineData("map")]
    [InlineData("viewport")]
    public void TransportParserAcceptsOnlyTheClosedCaptureGrammar(string mode)
    {
        string requestId = Guid.NewGuid().ToString("N");

        Assert.True(ReviewScreenshotTransportArguments.TryParse(
            ["screenshot", "capture", requestId, mode, "proof_1"],
            out string parsedRequestId,
            out ReviewScreenshotCaptureQuery? query,
            out string error), error);
        Assert.Equal(requestId, parsedRequestId);
        Assert.Equal(mode, query!.Mode);
        Assert.Equal("proof_1", query.Label);

        Assert.False(ReviewScreenshotTransportArguments.TryParse(
            ["screenshot", "capture", requestId, mode, "../escape"],
            out _, out _, out _));
        Assert.False(ReviewScreenshotTransportArguments.TryParse(
            ["screenshot", "capture", requestId, "desktop", "proof"],
            out _, out _, out _));
        Assert.False(ReviewScreenshotTransportArguments.TryParse(
            ["screenshot", "capture", requestId, mode, "proof", "extra"],
            out _, out _, out _));
    }

    [Fact]
    public void OperationRevalidatesTheLabelBeforeUsingTheRuntime()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path);

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(
            new ReviewScreenshotRequest(ReviewScreenshotKind.Map, "../escape"),
            runtime);

        Assert.False(result.Succeeded);
        Assert.Equal(0, runtime.FolderRequests);
        Assert.Equal(0, runtime.TakeRequests);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void OperationFailsClosedWhenTheGameCannotCapture(
        bool worldReady,
        bool canTakeScreenshots,
        bool screenshotBusy)
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path)
        {
            IsWorldReady = worldReady,
            CanTakeScreenshots = canTakeScreenshots,
            ScreenshotBusy = screenshotBusy,
        };

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(Map("blocked"), runtime);

        Assert.False(result.Succeeded);
        Assert.Equal(0, runtime.TakeRequests);
    }

    [Fact]
    public void OperationRefusesAnExistingConcreteTarget()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path);
        string expectedPath = Path.Combine(temporary.Path, "SDVKit-existing.png");
        runtime.Files.Add(expectedPath);

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(Map("existing"), runtime);

        Assert.False(result.Succeeded);
        Assert.Equal("screenshotAlreadyExists", result.ProblemCode);
        Assert.Contains(expectedPath, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, runtime.TakeRequests);
    }

    [Fact]
    public void TransportResponseIsAtomicBoundedAndContainsNoLocalPath()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewScreenshotCaptureQuery("viewport", "proof_1");
        ReviewScreenshotResponseEnvelope envelope = ReviewScreenshotResponse.Create(
            requestId,
            query,
            new ReviewScreenshotResult(
                true,
                $"Created '{temporary.Path}'.",
                FileName: ReviewScreenshotContract.FileName(query.Label)),
            new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

        ReviewScreenshotResponseFile.Write(temporary.Path, envelope);

        string responsePath = ReviewScreenshotContract.ResponsePath(
            temporary.Path,
            requestId);
        byte[] bytes = File.ReadAllBytes(responsePath);
        Assert.InRange(bytes.Length, 1, ReviewScreenshotContract.MaximumResponseBytes);
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(temporary.Path, json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal("ready", document.RootElement
            .GetProperty("report").GetProperty("state").GetString());
        Assert.Equal("SDVKit-proof_1.png", document.RootElement
            .GetProperty("report").GetProperty("fileName").GetString());
        Assert.False(File.Exists(responsePath + ".tmp"));

        Assert.Throws<InvalidDataException>(() =>
            ReviewScreenshotResponseFile.Write(temporary.Path, envelope));
    }

    [Fact]
    public void BlockedTransportResponseUsesOnlyTheFixedProblem()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewScreenshotCaptureQuery("map", "existing");
        ReviewScreenshotResponseEnvelope envelope = ReviewScreenshotResponse.Create(
            requestId,
            query,
            new ReviewScreenshotResult(
                false,
                $"Refusing path '{temporary.Path}'.",
                "screenshotAlreadyExists"),
            new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

        ReviewScreenshotResponseFile.Write(temporary.Path, envelope);

        string json = File.ReadAllText(
            ReviewScreenshotContract.ResponsePath(temporary.Path, requestId));
        Assert.DoesNotContain(temporary.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screenshotAlreadyExists", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Refusing path", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationRejectsAnUnexpectedReturnedFileName()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path)
        {
            ReturnedFileName = "SDVKit-review_2.png",
            CreateConcreteTarget = true,
        };

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(Map("review"), runtime);

        Assert.False(result.Succeeded);
        Assert.Equal(1, runtime.TakeRequests);
    }

    [Fact]
    public void OperationRejectsAReportedCaptureWithoutTheConcretePng()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path)
        {
            ReturnedFileName = "SDVKit-review.png",
        };

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(Map("review"), runtime);

        Assert.False(result.Succeeded);
        Assert.Equal(1, runtime.TakeRequests);
    }

    [Fact]
    public void OperationConfirmsTheExactPngAndReportsItsFullPath()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path)
        {
            ReturnedFileName = "SDVKit-Vanilla_1.png",
            CreateConcreteTarget = true,
        };

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(Map("Vanilla_1"), runtime);

        string expectedPath = Path.Combine(temporary.Path, "SDVKit-Vanilla_1.png");
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("SDVKit-Vanilla_1", runtime.RequestedScreenshotName);
        Assert.Equal(1, runtime.FolderRequests);
        Assert.Contains(expectedPath, result.Message, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(expectedPath));
    }

    [Fact]
    public void ViewportCaptureConfirmsTheExactPngWithoutUsingTheMapApi()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path)
        {
            CreateViewportTarget = true,
        };

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(
            new ReviewScreenshotRequest(ReviewScreenshotKind.Viewport, "menu"),
            runtime);

        string expectedPath = Path.Combine(temporary.Path, "SDVKit-menu.png");
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, runtime.TakeRequests);
        Assert.Equal(1, runtime.ViewportRequests);
        Assert.Equal(expectedPath, runtime.RequestedViewportPath);
        Assert.Contains("viewport screenshot", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewportCaptureWorksWithoutALoadedWorldOrMapCapability()
    {
        using TemporaryDirectory temporary = new();
        var runtime = new FakeRuntime(temporary.Path)
        {
            IsWorldReady = false,
            CanTakeScreenshots = false,
            ScreenshotBusy = true,
            CreateViewportTarget = true,
        };

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(
            new ReviewScreenshotRequest(ReviewScreenshotKind.Viewport, "title"),
            runtime);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0, runtime.TakeRequests);
        Assert.Equal(1, runtime.ViewportRequests);
    }

    [Fact]
    public void OneSdvkitRootRoutesAllReviewActions()
    {
        string source = ReadSource("ReviewScreenshotCommand.cs");
        string modEntry = ReadSource("ModEntry.cs");

        Assert.Equal(
            1,
            source.Split("ConsoleCommands.Add(", StringSplitOptions.None).Length - 1);
        Assert.Contains("private const string RootCommand = \"sdvkit\";", source, StringComparison.Ordinal);
        Assert.Contains("ReviewScreenshotCommand.Handle(", source, StringComparison.Ordinal);
        Assert.Contains("ReviewInputCommand.Handle(", source, StringComparison.Ordinal);
        Assert.Contains("ReviewFixtureCommand.Handle(", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDataCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewMapCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewTextureCommand.Handle(", source, StringComparison.Ordinal);
        Assert.Contains("ReviewModAssetCommand.Handle(", source, StringComparison.Ordinal);
        Assert.Contains("Events.Content.AssetRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("Events.Content.AssetReady +=", source, StringComparison.Ordinal);
        Assert.Contains("Events.Content.AssetsInvalidated +=", source, StringComparison.Ordinal);
        Assert.Contains("_texture.Format != SurfaceFormat.Color", ReadSource("ReviewTextureCommand.cs"), StringComparison.Ordinal);
        Assert.Contains("ReviewAudioCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewCommand.Register(", modEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewScreenshotCommand.Register(", modEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void TextureClassifierInitializationIsDeferredUntilClassification()
    {
        string source = ReadSource("ReviewTextureCommand.cs");
        int sourceConstructor = source.IndexOf(
            "public StardewReviewTextureSource(",
            StringComparison.Ordinal);
        int classifyMethod = source.IndexOf(
            "public bool TryClassifyTexture(",
            sourceConstructor,
            StringComparison.Ordinal);
        int classifierConstruction = source.IndexOf(
            "new ReviewTextureXnbClassifier(",
            sourceConstructor,
            StringComparison.Ordinal);

        Assert.True(sourceConstructor >= 0);
        Assert.True(classifyMethod > sourceConstructor);
        Assert.True(classifierConstruction > classifyMethod);
        Assert.True(
            source.Split(
                "when (!ReviewException.IsFatal(exception))",
                StringSplitOptions.None).Length - 1 >= 4);
    }

    private static ReviewScreenshotRequest Map(string label) =>
        new(ReviewScreenshotKind.Map, label);

    private static string ReadSource(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName,
                "src",
                "SdvKit.AlwaysOn",
                fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }

    private sealed class FakeRuntime(string screenshotFolder) : IReviewScreenshotRuntime
    {
        public bool IsWorldReady { get; init; } = true;

        public bool CanTakeScreenshots { get; init; } = true;

        public bool ScreenshotBusy { get; init; }

        public string? ReturnedFileName { get; init; }

        public bool CreateConcreteTarget { get; init; }

        public bool CreateViewportTarget { get; init; }

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int FolderRequests { get; private set; }

        public int TakeRequests { get; private set; }

        public int ViewportRequests { get; private set; }

        public string? RequestedScreenshotName { get; private set; }

        public string? RequestedViewportPath { get; private set; }

        public string GetScreenshotFolder()
        {
            FolderRequests++;
            return screenshotFolder;
        }

        public bool FileExists(string path) => Files.Contains(Path.GetFullPath(path));

        public string? TakeMapScreenshot(string screenshotName)
        {
            TakeRequests++;
            RequestedScreenshotName = screenshotName;
            if (CreateConcreteTarget)
            {
                Files.Add(Path.Combine(screenshotFolder, $"{screenshotName}.png"));
            }

            return ReturnedFileName;
        }

        public bool TryTakeViewportScreenshot(string path, out string error)
        {
            ViewportRequests++;
            RequestedViewportPath = path;
            if (CreateViewportTarget)
            {
                Files.Add(path);
            }

            error = CreateViewportTarget ? string.Empty : "capture failed";
            return CreateViewportTarget;
        }
    }
}

using SdvKit.AlwaysOn;

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
        Assert.Contains(expectedPath, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, runtime.TakeRequests);
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
    public void OneSdvkitRootRoutesScreenshotAndFixtureActions()
    {
        string source = ReadSource("ReviewScreenshotCommand.cs");
        string modEntry = ReadSource("ModEntry.cs");

        Assert.Equal(
            1,
            source.Split("ConsoleCommands.Add(", StringSplitOptions.None).Length - 1);
        Assert.Contains("private const string RootCommand = \"sdvkit\";", source, StringComparison.Ordinal);
        Assert.Contains("ReviewScreenshotCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewInputCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewFixtureCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDataCommand.Handle(arguments", source, StringComparison.Ordinal);
        Assert.Contains("ReviewCommand.Register(", modEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewScreenshotCommand.Register(", modEntry, StringComparison.Ordinal);
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

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
                out string label,
                out string acceptedError),
            acceptedError);
        Assert.Equal("Vanilla_1", label);

        Assert.False(ReviewScreenshotArguments.TryParse([], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["status"], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["Screenshot", "x"], out _, out _));
        Assert.False(ReviewScreenshotArguments.TryParse(["screenshot"], out _, out _));
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

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute("../escape", runtime);

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

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute("blocked", runtime);

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

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute("existing", runtime);

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

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute("review", runtime);

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

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute("review", runtime);

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

        ReviewScreenshotResult result = ReviewScreenshotOperation.Execute("Vanilla_1", runtime);

        string expectedPath = Path.Combine(temporary.Path, "SDVKit-Vanilla_1.png");
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("SDVKit-Vanilla_1", runtime.RequestedScreenshotName);
        Assert.Equal(1, runtime.FolderRequests);
        Assert.Contains(expectedPath, result.Message, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(expectedPath));
    }

    private sealed class FakeRuntime(string screenshotFolder) : IReviewScreenshotRuntime
    {
        public bool IsWorldReady { get; init; } = true;

        public bool CanTakeScreenshots { get; init; } = true;

        public bool ScreenshotBusy { get; init; }

        public string? ReturnedFileName { get; init; }

        public bool CreateConcreteTarget { get; init; }

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int FolderRequests { get; private set; }

        public int TakeRequests { get; private set; }

        public string? RequestedScreenshotName { get; private set; }

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
    }
}

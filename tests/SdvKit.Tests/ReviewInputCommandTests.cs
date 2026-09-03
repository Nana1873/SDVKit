using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewInputCommandTests
{
    [Theory]
    [InlineData("F8")]
    [InlineData("Enter")]
    [InlineData("MouseLeft")]
    [InlineData("ControllerA")]
    public void ParserAcceptsAnExactButtonPress(string button)
    {
        Assert.True(
            ReviewInputArguments.TryParse(
                ["input", "press", button],
                out ReviewInputRequest? request,
                out string error),
            error);
        Assert.Equal(ReviewInputKind.Press, request!.Kind);
        Assert.Equal(button, request.Button);
    }

    [Theory]
    [InlineData("MouseWheelUp")]
    [InlineData("MouseWheelDown")]
    [InlineData("mousewheeldown")]
    public void ParserAcceptsVirtualMouseWheelInput(string button)
    {
        Assert.True(
            ReviewInputArguments.TryParse(
                ["input", "press", button],
                out ReviewInputRequest? request,
                out string error),
            error);
        Assert.Equal(ReviewInputKind.Scroll, request!.Kind);
        Assert.Equal(button, request.Button);
    }

    [Fact]
    public void ParserAcceptsNonNegativeUiCursorCoordinates()
    {
        Assert.True(
            ReviewInputArguments.TryParse(
                ["input", "cursor", "1279", "719"],
                out ReviewInputRequest? request,
                out string error),
            error);
        Assert.Equal(ReviewInputKind.Cursor, request!.Kind);
        Assert.Equal(1279, request.X);
        Assert.Equal(719, request.Y);
    }

    [Fact]
    public void ParserAcceptsClearingTheVirtualCursor()
    {
        Assert.True(
            ReviewInputArguments.TryParse(
                ["input", "cursor", "clear"],
                out ReviewInputRequest? request,
                out string error),
            error);
        Assert.Equal(ReviewInputKind.ClearCursor, request!.Kind);
    }

    [Theory]
    [InlineData("Input", "press", "F8")]
    [InlineData("input", "Press", "F8")]
    [InlineData("input", "press", "Mouse-Left")]
    [InlineData("input", "cursor", "-1")]
    [InlineData("input", "cursor", "1.5")]
    public void ParserRejectsMalformedOrNonExactInput(params string[] arguments)
    {
        Assert.False(ReviewInputArguments.TryParse(arguments, out _, out _));
    }

    [Fact]
    public void OperationRequiresALoadedWorldBeforeDispatch()
    {
        var runtime = new FakeRuntime { IsWorldReady = false };

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Press, "F8", 0, 0),
            runtime);

        Assert.False(result.Succeeded);
        Assert.Equal(0, runtime.PressRequests);
    }

    [Fact]
    public void OperationReportsTheCanonicalPressedButton()
    {
        var runtime = new FakeRuntime { CanonicalButton = "ControllerA" };

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Press, "controllera", 0, 0),
            runtime);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, runtime.PressRequests);
        Assert.Contains("ControllerA", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MouseWheelUp", 120)]
    [InlineData("MouseWheelDown", -120)]
    public void OperationDispatchesOneMouseWheelNotch(string button, int expectedDirection)
    {
        var runtime = new FakeRuntime();

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Scroll, button, 0, 0),
            runtime);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, runtime.ScrollRequests);
        Assert.Equal(expectedDirection, runtime.ScrollDirection);
        Assert.Equal(0, runtime.PressRequests);
        Assert.Contains(button, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(1280, 0)]
    [InlineData(0, 720)]
    public void OperationRejectsCursorCoordinatesOutsideTheUiViewport(int x, int y)
    {
        var runtime = new FakeRuntime();

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Cursor, null, x, y),
            runtime);

        Assert.False(result.Succeeded);
        Assert.Equal(0, runtime.CursorRequests);
    }

    [Fact]
    public void OperationMovesTheCursorOnlyInsideTheUiViewport()
    {
        var runtime = new FakeRuntime();

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Cursor, null, 1279, 719),
            runtime);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, runtime.CursorRequests);
        Assert.Equal((1279, 719), runtime.Cursor);
    }

    [Fact]
    public void OperationClearsTheVirtualCursorWithoutALoadedWorld()
    {
        var runtime = new FakeRuntime { IsWorldReady = false };

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.ClearCursor, null, 0, 0),
            runtime);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, runtime.ClearRequests);
    }

    [Fact]
    public void SourceDoesNotMoveOrFocusThePhysicalPointer()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SdvKit.AlwaysOn",
            "ReviewInputCommand.cs"));

        Assert.DoesNotContain("SetCursorPos", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientToScreen", source, StringComparison.Ordinal);
        Assert.DoesNotContain("user32.dll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowHandle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.setMousePosition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppActivate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceReleasesSyntheticButtonsAfterTheCompletedGameUpdateOnly()
    {
        string repositoryRoot = FindRepositoryRoot();
        string inputSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SdvKit.AlwaysOn",
            "ReviewInputCommand.cs"));
        string entrySource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SdvKit.AlwaysOn",
            "ModEntry.cs"));

        int pressIndex = inputSource.IndexOf(
            "helper.Input.Press(parsed);",
            StringComparison.Ordinal);
        int pendingIndex = inputSource.IndexOf(
            "pendingButtonReleases.Add(parsed);",
            StringComparison.Ordinal);
        int releaseIndex = inputSource.IndexOf(
            "helper.Input.Suppress(button);",
            StringComparison.Ordinal);
        Assert.True(pressIndex >= 0 && pressIndex < pendingIndex);
        Assert.True(pendingIndex < releaseIndex);
        Assert.Contains("pendingButtonReleases.Clear();", inputSource, StringComparison.Ordinal);

        string updateTicking = SliceBetween(
            entrySource,
            "helper.Events.GameLoop.UpdateTicking +=",
            "helper.Events.GameLoop.OneSecondUpdateTicked +=");
        string updateTicked = SliceBetween(
            entrySource,
            "helper.Events.GameLoop.UpdateTicked +=",
            "helper.Events.GameLoop.GameLaunched +=");
        Assert.DoesNotContain("ReleasePendingPresses", updateTicking, StringComparison.Ordinal);
        Assert.Contains("_reviewInput?.ReleasePendingPresses();", updateTicked, StringComparison.Ordinal);
        Assert.Equal(
            3,
            entrySource.Split(
                "_reviewInput?.ReleasePendingPresses();",
                StringSplitOptions.None).Length - 1);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "SdvKit.AlwaysOn",
                    "SdvKit.AlwaysOn.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }

    private static string SliceBetween(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0);
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private sealed class FakeRuntime : IReviewInputRuntime
    {
        public bool IsWorldReady { get; init; } = true;

        public int UiWidth { get; init; } = 1280;

        public int UiHeight { get; init; } = 720;

        public string CanonicalButton { get; init; } = "F8";

        public int PressRequests { get; private set; }

        public int CursorRequests { get; private set; }

        public int ScrollRequests { get; private set; }

        public int ScrollDirection { get; private set; }

        public int ClearRequests { get; private set; }

        public (int X, int Y)? Cursor { get; private set; }

        public bool TryPress(string button, out string canonicalButton, out string error)
        {
            PressRequests++;
            canonicalButton = CanonicalButton;
            error = string.Empty;
            return true;
        }

        public bool TrySetCursor(int x, int y, out string error)
        {
            CursorRequests++;
            Cursor = (x, y);
            error = string.Empty;
            return true;
        }

        public bool TryScroll(int direction, out string error)
        {
            ScrollRequests++;
            ScrollDirection = direction;
            error = string.Empty;
            return true;
        }

        public bool TryClearCursor(out string error)
        {
            ClearRequests++;
            Cursor = null;
            error = string.Empty;
            return true;
        }
    }
}

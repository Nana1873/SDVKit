using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewInputCommandTests
{
    private const string RequestId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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
    [InlineData("press", "MouseLeft", 0)]
    [InlineData("wheel", "up", 1)]
    [InlineData("wheel", "down", 1)]
    public void ParserAcceptsRequestBoundActions(
        string action,
        string value,
        int expectedKind)
    {
        Assert.True(
            ReviewInputArguments.TryParse(
                ["input", "request", RequestId, action, value],
                out ReviewInputRequest? request,
                out string error),
            error);
        Assert.Equal((ReviewInputKind)expectedKind, request!.Kind);
        Assert.Equal(RequestId, request.RequestId);
    }

    [Fact]
    public void ParserAcceptsRequestBoundCursorLifecycle()
    {
        Assert.True(ReviewInputArguments.TryParse(
            ["input", "request", RequestId, "cursor", "20", "30"],
            out ReviewInputRequest? set,
            out string setError), setError);
        Assert.Equal(ReviewInputKind.Cursor, set!.Kind);
        Assert.Equal(RequestId, set.RequestId);

        Assert.True(ReviewInputArguments.TryParse(
            ["input", "request", RequestId, "cursor", "clear"],
            out ReviewInputRequest? clear,
            out string clearError), clearError);
        Assert.Equal(ReviewInputKind.ClearCursor, clear!.Kind);
        Assert.Equal(RequestId, clear.RequestId);
    }

    [Theory]
    [InlineData("MouseWheelUp")]
    [InlineData("MouseWheelDown")]
    public void RequestBoundPressRejectsWheelAliases(string button)
    {
        Assert.False(ReviewInputArguments.TryParse(
            ["input", "request", RequestId, "press", button],
            out _,
            out _));
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
    public void OperationDispatchesButtonInputWithoutALoadedWorldGate()
    {
        var runtime = new FakeRuntime();

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Press, "F8", 0, 0),
            runtime);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, runtime.PressRequests);
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
    [InlineData("MouseLeft")]
    [InlineData("mouseright")]
    [InlineData("MouseMiddle")]
    [InlineData("MouseX1")]
    [InlineData("MouseX2")]
    public void OperationRejectsMouseButtonsUntilTheVirtualCursorIsSet(string button)
    {
        var runtime = new FakeRuntime { CursorSet = false };

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Press, button, 0, 0),
            runtime);

        Assert.False(result.Succeeded);
        Assert.Equal("inputCursorMissing", result.ProblemCode);
        Assert.Equal(0, runtime.PressRequests);
    }

    [Fact]
    public void OperationRejectsPressWhenTheBackgroundInputAdapterIsNotReady()
    {
        var runtime = new FakeRuntime { InputAdapterReady = false };

        ReviewInputResult result = ReviewInputOperation.Execute(
            new ReviewInputRequest(ReviewInputKind.Press, "F8", 0, 0),
            runtime);

        Assert.False(result.Succeeded);
        Assert.Equal("inputAdapterUnavailable", result.ProblemCode);
        Assert.Equal(0, runtime.PressRequests);
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
    public void OperationMovesTheCursorInsideTheUiViewportWithoutALoadedWorldGate()
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
        var runtime = new FakeRuntime();

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
        Assert.DoesNotContain("Context.IsWorldReady", source, StringComparison.Ordinal);
        Assert.Contains(
            "WindowsForegroundWindowProbe.Observe()",
            File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "src",
                "SdvKit.AlwaysOn",
                "ModEntry.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLetsSmapiCompleteTheBoundedBackgroundInputLifecycle()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SdvKit.AlwaysOn",
            "ReviewInputCommand.cs"));

        Assert.Contains("helper.Input.Press(parsed);", source, StringComparison.Ordinal);
        Assert.Contains("ReviewVirtualCursor.IsInstalled", source, StringComparison.Ordinal);
        Assert.Contains("ReviewVirtualCursor.IsSet", source, StringComparison.Ordinal);
        Assert.Contains("typeof(Microsoft.Xna.Framework.Game)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(Microsoft.Xna.Framework.Game.IsActive)", source, StringComparison.Ordinal);
        Assert.Contains("typeof(Game1)", source, StringComparison.Ordinal);
        Assert.Contains("nameof(Game1.IsActiveNoOverlay)", source, StringComparison.Ordinal);
        Assert.Contains("new Harmony(HarmonyId).UnpatchAll(HarmonyId);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("helper.Input.Suppress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingButtonReleases", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleasePendingPresses", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseFileIsRequestBoundCreateNewAndBounded()
    {
        using TemporaryDirectory temporary = new();
        var envelope = new ReviewInputResponseEnvelope(
            ReviewInputContract.SchemaVersion,
            RequestId,
            DateTimeOffset.UtcNow,
            42,
            ReviewInputContract.CursorSetAction,
            true,
            null,
            null,
            10,
            20,
            true,
            false,
            null);

        ReviewInputResponseFile.Write(temporary.Path, envelope);

        string path = ReviewInputContract.ResponsePath(temporary.Path, RequestId);
        Assert.True(File.Exists(path));
        Assert.InRange(
            new FileInfo(path).Length,
            1,
            ReviewInputContract.MaximumResponseBytes);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Throws<InvalidDataException>(() =>
            ReviewInputResponseFile.Write(temporary.Path, envelope));
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

    private sealed class FakeRuntime : IReviewInputRuntime
    {
        public int UiWidth { get; init; } = 1280;

        public int UiHeight { get; init; } = 720;

        public bool InputAdapterReady { get; init; } = true;

        public bool CursorSet { get; init; } = true;

        public bool MenuOpen { get; init; } = true;

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

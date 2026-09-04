#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal static class ReviewScreenshotArguments
{
    internal const string Usage =
        "Usage: sdvkit screenshot <label> | sdvkit screenshot viewport <label>";
    internal const string LabelError =
        "A screenshot label must contain 1-64 ASCII letters, digits, '-' or '_' only.";

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out ReviewScreenshotRequest? request,
        out string error)
    {
        request = null;
        if (arguments is null
            || arguments.Count < 2
            || !string.Equals(arguments[0], "screenshot", StringComparison.Ordinal))
        {
            error = Usage;
            return false;
        }

        ReviewScreenshotKind kind;
        string label;
        if (arguments.Count == 2)
        {
            kind = ReviewScreenshotKind.Map;
            label = arguments[1];
        }
        else if (arguments.Count == 3
            && string.Equals(arguments[1], "viewport", StringComparison.Ordinal))
        {
            kind = ReviewScreenshotKind.Viewport;
            label = arguments[2];
        }
        else
        {
            error = Usage;
            return false;
        }

        if (!IsValidLabel(label))
        {
            error = LabelError;
            return false;
        }

        request = new ReviewScreenshotRequest(kind, label);
        error = string.Empty;
        return true;
    }

    public static bool IsValidLabel(string? label)
    {
        if (label is null || label.Length is < 1 or > 64)
        {
            return false;
        }

        return label.All(character =>
            (character >= 'a' && character <= 'z')
            || (character >= 'A' && character <= 'Z')
            || (character >= '0' && character <= '9')
            || character is '-' or '_');
    }
}

internal enum ReviewScreenshotKind
{
    Map,
    Viewport,
}

internal sealed record ReviewScreenshotRequest(
    ReviewScreenshotKind Kind,
    string Label);

internal interface IReviewScreenshotRuntime
{
    bool IsWorldReady { get; }

    bool CanTakeScreenshots { get; }

    bool ScreenshotBusy { get; }

    string GetScreenshotFolder();

    bool FileExists(string path);

    string? TakeMapScreenshot(string screenshotName);

    bool TryTakeViewportScreenshot(string path, out string error);
}

internal sealed record ReviewScreenshotResult(
    bool Succeeded,
    string Message);

internal static class ReviewScreenshotOperation
{
    public static ReviewScreenshotResult Execute(
        ReviewScreenshotRequest request,
        IReviewScreenshotRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!Enum.IsDefined(request.Kind)
            || !ReviewScreenshotArguments.IsValidLabel(request.Label))
        {
            return Failure(ReviewScreenshotArguments.LabelError);
        }

        if (request.Kind == ReviewScreenshotKind.Map
            && !runtime.IsWorldReady)
        {
            return Failure("A world must be loaded before taking a map review screenshot.");
        }

        if (request.Kind == ReviewScreenshotKind.Map
            && (!runtime.CanTakeScreenshots || runtime.ScreenshotBusy))
        {
            return Failure("Stardew cannot take a map screenshot right now.");
        }

        string screenshotName = $"SDVKit-{request.Label}";
        string screenshotFileName = $"{screenshotName}.png";
        string screenshotFolder = runtime.GetScreenshotFolder();
        if (string.IsNullOrWhiteSpace(screenshotFolder)
            || !Path.IsPathFullyQualified(screenshotFolder))
        {
            return Failure("Stardew returned an invalid screenshot folder.");
        }

        screenshotFolder = Path.GetFullPath(screenshotFolder);
        string expectedPath = Path.Combine(screenshotFolder, screenshotFileName);
        if (runtime.FileExists(expectedPath))
        {
            return Failure(
                $"Refusing to overwrite existing isolated screenshot '{expectedPath}'.");
        }

        if (request.Kind == ReviewScreenshotKind.Viewport)
        {
            if (!runtime.TryTakeViewportScreenshot(expectedPath, out string viewportError)
                || !runtime.FileExists(expectedPath))
            {
                return Failure(
                    $"Stardew failed to create the requested viewport screenshot '{expectedPath}': "
                    + viewportError);
            }

            return new ReviewScreenshotResult(
                true,
                $"Created isolated viewport screenshot '{expectedPath}'.");
        }

        string? writtenFileName = runtime.TakeMapScreenshot(screenshotName);
        if (!string.Equals(writtenFileName, screenshotFileName, StringComparison.Ordinal)
            || !runtime.FileExists(expectedPath))
        {
            return Failure(
                $"Stardew failed to create the requested map screenshot '{expectedPath}'.");
        }

        return new ReviewScreenshotResult(
            true,
            $"Created isolated map screenshot '{expectedPath}'.");
    }

    private static ReviewScreenshotResult Failure(string message) => new(false, message);
}

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewCommand
{
    private const string RootCommand = "sdvkit";
    private const string HelpText =
        "Isolated review helpers: sdvkit screenshot ... | sdvkit input ... | sdvkit fixture ... | sdvkit data ... | sdvkit map ...";
    private const string Usage =
        "Usage: sdvkit screenshot ... | sdvkit input ... | sdvkit fixture ... | sdvkit data ... | sdvkit map ...";

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        string runtimePath,
        Func<TestSaveAutomation?> testSave,
        Func<NetworkTwoAutomation?> networkTwo)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(testSave);
        ArgumentNullException.ThrowIfNull(networkTwo);

        var screenshotRuntime = new StardewReviewScreenshotRuntime();
        var inputRuntime = new StardewReviewInputRuntime(helper);
        var dataSource = new StardewReviewDataSource(helper);
        var mapSource = new StardewReviewMapSource(helper);
        var fixtureRuntime = new StardewReviewFixtureRuntime(
            testSave,
            networkTwo,
            helper.Multiplayer.GetNewID);
        helper.ConsoleCommands.Add(
            RootCommand,
            HelpText,
            (_, arguments) =>
            {
                if (arguments.Length > 0
                    && string.Equals(arguments[0], "screenshot", StringComparison.Ordinal))
                {
                    ReviewScreenshotCommand.Handle(arguments, screenshotRuntime, monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "input", StringComparison.Ordinal))
                {
                    ReviewInputCommand.Handle(arguments, inputRuntime, monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "fixture", StringComparison.Ordinal))
                {
                    ReviewFixtureCommand.Handle(arguments, fixtureRuntime, monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "data", StringComparison.Ordinal))
                {
                    ReviewDataCommand.Handle(arguments, dataSource, runtimePath, monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "map", StringComparison.Ordinal))
                {
                    ReviewMapCommand.Handle(arguments, mapSource, runtimePath, monitor);
                }
                else
                {
                    monitor.Log(Usage, LogLevel.Error);
                }
            });
    }
}

internal static class ReviewScreenshotCommand
{
    public static void Handle(
        string[] arguments,
        IReviewScreenshotRuntime runtime,
        IMonitor monitor)
    {
        if (!ReviewScreenshotArguments.TryParse(
                arguments,
                out ReviewScreenshotRequest? request,
                out string error))
        {
            monitor.Log(error, LogLevel.Error);
            return;
        }

        try
        {
            ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(request!, runtime);
            monitor.Log(
                result.Message,
                result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit screenshot command failed without creating a confirmed PNG: {exception.Message}",
                LogLevel.Error);
        }
    }

}

internal sealed class StardewReviewScreenshotRuntime : IReviewScreenshotRuntime
{
    public bool IsWorldReady => Context.IsWorldReady;

    public bool CanTakeScreenshots => Game1.game1.CanTakeScreenshots();

    public bool ScreenshotBusy => Game1.game1.ScreenshotBusy;

    public string GetScreenshotFolder() =>
        Game1.game1.GetScreenshotFolder(true);

    public bool FileExists(string path) => File.Exists(path);

    public string? TakeMapScreenshot(string screenshotName) =>
        Game1.game1.takeMapScreenshot(1f, screenshotName, null!);

    public bool TryTakeViewportScreenshot(string path, out string error)
    {
        try
        {
            GraphicsDevice graphicsDevice = Game1.graphics.GraphicsDevice;
            PresentationParameters presentation = graphicsDevice.PresentationParameters;
            int width = presentation.BackBufferWidth;
            int height = presentation.BackBufferHeight;
            if (width <= 0 || height <= 0)
            {
                error = "the graphics backbuffer has invalid dimensions";
                return false;
            }

            var pixels = new Color[checked(width * height)];
            graphicsDevice.GetBackBufferData(pixels);
            using var texture = new Texture2D(
                graphicsDevice,
                width,
                height,
                false,
                SurfaceFormat.Color);
            texture.SetData(pixels);
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            texture.SaveAsPng(stream, width, height);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }
}
#endif

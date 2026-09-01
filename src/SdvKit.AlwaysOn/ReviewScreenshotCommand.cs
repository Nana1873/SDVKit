#if SDVKIT_GAME_AVAILABLE
using StardewModdingAPI;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal static class ReviewScreenshotArguments
{
    internal const string Usage = "Usage: sdvkit screenshot <label>";
    internal const string LabelError =
        "A screenshot label must contain 1-64 ASCII letters, digits, '-' or '_' only.";

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out string label,
        out string error)
    {
        label = string.Empty;
        if (arguments is null
            || arguments.Count != 2
            || !string.Equals(arguments[0], "screenshot", StringComparison.Ordinal))
        {
            error = Usage;
            return false;
        }

        if (!IsValidLabel(arguments[1]))
        {
            error = LabelError;
            return false;
        }

        label = arguments[1];
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

internal interface IReviewScreenshotRuntime
{
    bool IsWorldReady { get; }

    bool CanTakeScreenshots { get; }

    bool ScreenshotBusy { get; }

    string GetScreenshotFolder();

    bool FileExists(string path);

    string? TakeMapScreenshot(string screenshotName);
}

internal sealed record ReviewScreenshotResult(
    bool Succeeded,
    string Message);

internal static class ReviewScreenshotOperation
{
    public static ReviewScreenshotResult Execute(
        string label,
        IReviewScreenshotRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!ReviewScreenshotArguments.IsValidLabel(label))
        {
            return Failure(ReviewScreenshotArguments.LabelError);
        }

        if (!runtime.IsWorldReady)
        {
            return Failure("A world must be loaded before taking a map screenshot.");
        }

        if (!runtime.CanTakeScreenshots || runtime.ScreenshotBusy)
        {
            return Failure("Stardew cannot take a map screenshot right now.");
        }

        string screenshotName = $"SDVKit-{label}";
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

        string? writtenFileName = runtime.TakeMapScreenshot(screenshotName);
        if (!string.Equals(
                writtenFileName,
                screenshotFileName,
                StringComparison.Ordinal)
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
        "Isolated review helpers: sdvkit screenshot <label> | sdvkit fixture ...";
    private const string Usage =
        "Usage: sdvkit screenshot <label> | sdvkit fixture ...";

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        Func<TestSaveAutomation?> testSave,
        Func<NetworkTwoAutomation?> networkTwo)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(testSave);
        ArgumentNullException.ThrowIfNull(networkTwo);

        var screenshotRuntime = new StardewReviewScreenshotRuntime();
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
                    && string.Equals(arguments[0], "fixture", StringComparison.Ordinal))
                {
                    ReviewFixtureCommand.Handle(arguments, fixtureRuntime, monitor);
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
                out string label,
                out string error))
        {
            monitor.Log(error, LogLevel.Error);
            return;
        }

        try
        {
            ReviewScreenshotResult result = ReviewScreenshotOperation.Execute(label, runtime);
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
}
#endif

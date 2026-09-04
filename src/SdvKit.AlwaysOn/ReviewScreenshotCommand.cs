#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
#endif
using System.Text.Json;
using SdvKit.Cli.LiveLab;

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
        => ReviewScreenshotContract.IsLabel(label);
}

internal static class ReviewScreenshotTransportArguments
{
    internal const string Usage =
        "Usage: sdvkit screenshot capture <request-id> <map|viewport> <label>";

    public static bool IsTransport(IReadOnlyList<string>? arguments) =>
        arguments is { Count: >= 2 }
        && string.Equals(arguments[0], "screenshot", StringComparison.Ordinal)
        && string.Equals(arguments[1], "capture", StringComparison.Ordinal);

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out string requestId,
        out ReviewScreenshotCaptureQuery? query,
        out string error)
    {
        requestId = string.Empty;
        query = null;
        if (!IsTransport(arguments)
            || arguments!.Count != 5
            || !ReviewTransportToken.IsRequestId(arguments[2])
            || !ReviewScreenshotContract.IsMode(arguments[3])
            || !ReviewScreenshotContract.IsLabel(arguments[4]))
        {
            error = Usage;
            return false;
        }

        requestId = arguments[2];
        query = new ReviewScreenshotCaptureQuery(arguments[3], arguments[4]);
        error = string.Empty;
        return true;
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
    string Message,
    string? ProblemCode = null,
    string? FileName = null);

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
            return Failure(
                "screenshotRequestInvalid",
                ReviewScreenshotArguments.LabelError);
        }

        if (request.Kind == ReviewScreenshotKind.Map
            && !runtime.IsWorldReady)
        {
            return Failure(
                "screenshotWorldNotReady",
                "A world must be loaded before taking a map review screenshot.");
        }

        if (request.Kind == ReviewScreenshotKind.Map
            && (!runtime.CanTakeScreenshots || runtime.ScreenshotBusy))
        {
            return Failure(
                "screenshotUnavailable",
                "Stardew cannot take a map screenshot right now.");
        }

        string screenshotName = $"SDVKit-{request.Label}";
        string screenshotFileName = ReviewScreenshotContract.FileName(request.Label);
        string screenshotFolder = runtime.GetScreenshotFolder();
        if (string.IsNullOrWhiteSpace(screenshotFolder)
            || !Path.IsPathFullyQualified(screenshotFolder))
        {
            return Failure(
                "screenshotPathInvalid",
                "Stardew returned an invalid screenshot folder.");
        }

        screenshotFolder = Path.GetFullPath(screenshotFolder);
        string expectedPath = Path.Combine(screenshotFolder, screenshotFileName);
        if (runtime.FileExists(expectedPath))
        {
            return Failure(
                "screenshotAlreadyExists",
                $"Refusing to overwrite existing isolated screenshot '{expectedPath}'.");
        }

        if (request.Kind == ReviewScreenshotKind.Viewport)
        {
            if (!runtime.TryTakeViewportScreenshot(expectedPath, out string viewportError)
                || !runtime.FileExists(expectedPath))
            {
                return Failure(
                    "screenshotCaptureFailed",
                    $"Stardew failed to create the requested viewport screenshot '{expectedPath}': "
                    + viewportError);
            }

            return new ReviewScreenshotResult(
                true,
                $"Created isolated viewport screenshot '{expectedPath}'.",
                FileName: screenshotFileName);
        }

        string? writtenFileName = runtime.TakeMapScreenshot(screenshotName);
        if (!string.Equals(writtenFileName, screenshotFileName, StringComparison.Ordinal)
            || !runtime.FileExists(expectedPath))
        {
            return Failure(
                "screenshotCaptureFailed",
                $"Stardew failed to create the requested map screenshot '{expectedPath}'.");
        }

        return new ReviewScreenshotResult(
            true,
            $"Created isolated map screenshot '{expectedPath}'.",
            FileName: screenshotFileName);
    }

    private static ReviewScreenshotResult Failure(string code, string message) =>
        new(false, message, code);
}

internal static class ReviewScreenshotResponse
{
    public static ReviewScreenshotResponseEnvelope Create(
        string requestId,
        ReviewScreenshotCaptureQuery query,
        ReviewScreenshotResult result,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(result);
        if (!ReviewTransportToken.IsRequestId(requestId)
            || !ReviewScreenshotContract.IsMode(query.Mode)
            || !ReviewScreenshotContract.IsLabel(query.Label))
        {
            throw new ArgumentException("The review-screenshot response identity is invalid.");
        }

        IReadOnlyList<ReviewScreenshotProblem> problems = result.Succeeded
            ? []
            : [Problem(result.ProblemCode)];
        return new ReviewScreenshotResponseEnvelope(
            ReviewScreenshotContract.SchemaVersion,
            requestId,
            new ReviewScreenshotReport(
                ReviewScreenshotContract.SchemaVersion,
                result.Succeeded ? "ready" : "blocked",
                query.Mode,
                query.Label,
                result.Succeeded ? ReviewScreenshotContract.FileName(query.Label) : null,
                capturedAtUtc,
                problems));
    }

    private static ReviewScreenshotProblem Problem(string? code) => code switch
    {
        "screenshotWorldNotReady" => new(
            code,
            "A world must be loaded before taking a map screenshot."),
        "screenshotUnavailable" => new(
            code,
            "Stardew cannot take a map screenshot right now."),
        "screenshotPathInvalid" => new(
            code,
            "The isolated screenshot destination is invalid."),
        "screenshotAlreadyExists" => new(
            code,
            "The exact isolated screenshot target already exists; it was not overwritten."),
        "screenshotRequestInvalid" => new(
            code,
            "The screenshot request is invalid."),
        _ => new(
            "screenshotCaptureFailed",
            "Stardew did not create a confirmed screenshot for the request."),
    };
}

internal static class ReviewScreenshotResponseFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Write(
        string runtimePath,
        ReviewScreenshotResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        EnsureRegularDirectory(absoluteRuntimePath);
        string responsePath = ReviewScreenshotContract.ResponsePath(
            absoluteRuntimePath,
            envelope.RequestId);
        string temporaryPath = responsePath + ".tmp";
        if (EntryExists(responsePath) || EntryExists(temporaryPath))
        {
            throw new InvalidDataException(
                "The review-screenshot response target already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length is < 1 or > ReviewScreenshotContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The review-screenshot response exceeds its bounded maximum.");
        }

        var ownsTemporary = false;
        var ownsResponse = false;
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                ownsTemporary = true;
                EnsureRegularFile(temporaryPath);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            EnsureRegularFile(temporaryPath);
            File.Move(temporaryPath, responsePath);
            ownsTemporary = false;
            ownsResponse = true;
            EnsureRegularFile(responsePath);
            ownsResponse = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                TryDeleteOwnedRegularFile(temporaryPath);
            }
            if (ownsResponse)
            {
                TryDeleteOwnedRegularFile(responsePath);
            }
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureRegularDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                "The review-screenshot response is not a regular file.");
        }
    }

    private static void TryDeleteOwnedRegularFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0
                && (attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            // The unique owned path is already absent.
        }
    }
}

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewCommand
{
    private const string RootCommand = "sdvkit";
    private const string HelpText =
        "Isolated review helpers: sdvkit screenshot ... | sdvkit input ... | sdvkit fixture ... | bounded data/map/texture/audio/mod-assets transports";
    private const string Usage =
        "Usage: sdvkit screenshot ... | sdvkit input ... | sdvkit fixture ... | sdvkit data ... | sdvkit map ... | sdvkit texture ... | sdvkit audio ... | sdvkit mod-assets ...";

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
        var textureSource = new StardewReviewTextureSource(helper);
        var audioSource = new StardewReviewAudioSource(helper);
        var modAssetSource = new StardewReviewModAssetSource(helper);
        helper.Events.Content.AssetRequested += modAssetSource.OnAssetRequested;
        helper.Events.Content.AssetReady += modAssetSource.OnAssetReady;
        helper.Events.Content.AssetsInvalidated += modAssetSource.OnAssetsInvalidated;
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
                    ReviewScreenshotCommand.Handle(
                        arguments,
                        screenshotRuntime,
                        runtimePath,
                        monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "input", StringComparison.Ordinal))
                {
                    ReviewInputCommand.Handle(
                        arguments,
                        inputRuntime,
                        runtimePath,
                        monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "fixture", StringComparison.Ordinal))
                {
                    ReviewFixtureCommand.Handle(
                        arguments,
                        fixtureRuntime,
                        monitor,
                        runtimePath,
                        testSave);
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
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "texture", StringComparison.Ordinal))
                {
                    ReviewTextureCommand.Handle(
                        arguments,
                        textureSource,
                        runtimePath,
                        monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "audio", StringComparison.Ordinal))
                {
                    ReviewAudioCommand.Handle(arguments, audioSource, runtimePath, monitor);
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "mod-assets", StringComparison.Ordinal))
                {
                    ReviewModAssetCommand.Handle(
                        arguments,
                        modAssetSource,
                        runtimePath,
                        monitor);
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
        string runtimePath,
        IMonitor monitor)
    {
        if (ReviewScreenshotTransportArguments.IsTransport(arguments))
        {
            HandleTransport(arguments, runtime, runtimePath, monitor);
            return;
        }

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

    private static void HandleTransport(
        string[] arguments,
        IReviewScreenshotRuntime runtime,
        string runtimePath,
        IMonitor monitor)
    {
        if (!ReviewScreenshotTransportArguments.TryParse(
                arguments,
                out string requestId,
                out ReviewScreenshotCaptureQuery? query,
                out string error))
        {
            monitor.Log(error, LogLevel.Error);
            return;
        }

        ReviewScreenshotKind kind = string.Equals(
            query!.Mode,
            ReviewScreenshotContract.MapMode,
            StringComparison.Ordinal)
                ? ReviewScreenshotKind.Map
                : ReviewScreenshotKind.Viewport;
        ReviewScreenshotResult result;
        try
        {
            result = ReviewScreenshotOperation.Execute(
                new ReviewScreenshotRequest(kind, query.Label),
                runtime);
        }
        catch (Exception exception)
        {
            result = new ReviewScreenshotResult(
                false,
                $"SDVKit screenshot command failed without creating a confirmed PNG: {exception.Message}",
                "screenshotCaptureFailed");
        }

        try
        {
            ReviewScreenshotResponseFile.Write(
                runtimePath,
                ReviewScreenshotResponse.Create(
                    requestId,
                    query,
                    result,
                    DateTimeOffset.UtcNow));
            monitor.Log(
                result.Message,
                result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit screenshot response failed closed: {exception.Message}",
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

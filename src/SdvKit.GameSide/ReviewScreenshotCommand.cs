using System.Text.Json;
using SdvKit.Cli.LiveLab;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.GameSide;

internal static class ReviewCommand
{
    private const string RootCommand = "sdvkit";
    private const string HelpText =
        "Isolated review helpers: sdvkit split-screen join|leave|status | sdvkit network leave|join|status | sdvkit screenshot ... | sdvkit input ... | sdvkit fixture ... | bounded screen/inventory/menu/data/map/texture/audio/mod-assets transports";
    private const string Usage =
        "Usage: sdvkit split-screen join|leave|status | sdvkit network leave|join|status | sdvkit screenshot ... | sdvkit input ... | sdvkit fixture ... | sdvkit inventory ... | sdvkit menu ... | sdvkit data ... | sdvkit map ... | sdvkit texture ... | sdvkit audio ... | sdvkit mod-assets ...";

    public static void Register(
        IModHelper helper,
        IMonitor monitor,
        string runtimePath,
        Func<TestSaveAutomation?> testSave,
        Func<NetworkTwoAutomation?> networkTwo,
        Action<Game1> prepareScreenExit,
        ReviewCommandQueue? commandQueue = null)
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
        var dataSource = new StardewReviewDataSource(helper);
        var menuCommands = new StardewModdingAPI.Utilities.PerScreen<ReviewMenuCommand>(() => new());
        var inputRuntimes = new StardewModdingAPI.Utilities.PerScreen<StardewReviewInputRuntime>(() => new(helper,
            menuCommands.Value.CurrentRevision, menuCommands.Value.CurrentContinuity,
            menuCommands.Value.CurrentViewport, menuCommands.Value.CurrentTextField));
        helper.Events.Display.MenuChanged += (_, e) => menuCommands.Value.ObserveRoot(e.NewMenu);
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
        helper.ConsoleCommands.Add(RootCommand, HelpText, (_, arguments) => Dispatch(arguments));
        if (commandQueue is not null)
        {
            helper.Events.GameLoop.UpdateTicking += (_, _) =>
                // SMAPI's Context.HasScreenId still reports a screen briefly after a local player
                // leaves; the game runner's instances are authoritative.
                commandQueue.Drain(
                    Context.ScreenId,
                    id => GameRunner.instance.gameInstances.Any(game => game.instanceId == id),
                    command => ReviewCommandLine.IsSdvKitCommand(command[0])
                        ? Dispatch(command[1..])
                        : SmapiConsoleCommands.TryRun(command[0], command[1..], monitor));
        }

        bool Dispatch(string[] arguments)
        {
                if (!ReviewScreenBindingCommand.TryValidateDispatch(
                        arguments,
                        testSave(),
                        out arguments,
                        out string bindingError))
                {
                    monitor.Log($"SDVKit screen-bound dispatch rejected: {bindingError}", LogLevel.Error);
                    return false;
                }
                if (Context.IsSplitScreen && (testSave() is not { } owned
                    || !owned.TryVerifyReviewFixture(out _, out _)))
                {
                    monitor.Log("SDVKit requires the exact owned local screen before dispatching a split-screen command.", LogLevel.Error);
                    return false;
                }
                if (arguments.Length > 0 && arguments[0] == "split-screen")
                {
                    try
                    {
                        TestSaveAutomation fixture = testSave()
                            ?? throw new InvalidOperationException("Local split-screen requires an owned disposable fixture review.");
                        fixture.HandleLocalSplitScreen(arguments, monitor, menuCommands.Value, prepareScreenExit);
                    }
                    catch (Exception exception)
                    {
                        monitor.Log($"SDVKit local split-screen rejected: {exception.GetBaseException().Message}", LogLevel.Error);
                    }
                }
                else if (arguments.Length > 0 && arguments[0] == "network")
                {
                    try
                    {
                        NetworkTwoAutomation network = networkTwo()
                            ?? throw new InvalidOperationException("Network lifecycle requires an owned network-2 review.");
                        network.HandleLifecycleCommand(arguments);
                    }
                    catch (Exception exception)
                    {
                        monitor.Log($"SDVKit network lifecycle rejected: {exception.GetBaseException().Message}", LogLevel.Error);
                    }
                }
                else if (arguments.Length > 0
                    && string.Equals(arguments[0], "screen-binding", StringComparison.Ordinal))
                {
                    ReviewScreenBindingCommand.Handle(arguments, runtimePath, testSave(), monitor);
                }
                else if (arguments.Length > 0
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
                        inputRuntimes.Value,
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
                else if (arguments.Length > 0 && arguments[0] == "progression")
                {
                    ReviewProgressionCommand.Handle(arguments, runtimePath, monitor, testSave);
                }
                else if (arguments.Length > 0 && arguments[0] == "shop")
                {
                    ReviewShopCommand.Handle(arguments, runtimePath, monitor, menuCommands.Value);
                }
                else if (arguments.Length > 0 && arguments[0] == "inventory")
                {
                    ReviewInventoryCommand.Handle(arguments, runtimePath, monitor);
                }
                else if (arguments.Length > 0 && arguments[0] == "world-act")
                {
                    ReviewWorldActCommand.Handle(arguments, runtimePath, monitor,
                        inputRuntimes.Value, testSave);
                }
                else if (arguments.Length > 0 && arguments[0] == "world-move")
                {
                    ReviewWorldMoveCommand.Handle(arguments, runtimePath, monitor,
                        inputRuntimes.Value, testSave);
                }
                else if (arguments.Length > 0 && arguments[0] == "world-action")
                {
                    ReviewWorldActionCommand.Handle(arguments, runtimePath, monitor,
                        inputRuntimes.Value, testSave);
                }
                else if (arguments.Length > 0 && arguments[0] == "world")
                {
                    ReviewWorldCommand.Handle(arguments, runtimePath, monitor);
                }
                else if (arguments.Length > 0 && arguments[0] == "container")
                {
                    ReviewContainerCommand.Handle(arguments, runtimePath, monitor, menuCommands.Value);
                }
                else if (arguments.Length > 0 && arguments[0] == "container-transfer")
                {
                    ReviewContainerTransferCommand.Handle(arguments, runtimePath, monitor, helper, menuCommands.Value, testSave);
                }
                else if (arguments.Length > 0 && arguments[0] == "menu")
                {
                    menuCommands.Value.Handle(arguments, runtimePath, monitor);
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
                    return false;
                }

                return true;
        }
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
            ReviewScreenshotResponsePublisher.Write(
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
            Rectangle bounds = Context.IsSplitScreen ? Game1.game1.localMultiplayerWindow
                : new Rectangle(0, 0, presentation.BackBufferWidth, presentation.BackBufferHeight);
            int width = bounds.Width;
            int height = bounds.Height;
            if (width <= 0 || height <= 0)
            {
                error = "the graphics backbuffer has invalid dimensions";
                return false;
            }

            var pixels = new Color[checked(width * height)];
            graphicsDevice.GetBackBufferData(bounds, pixels, 0, pixels.Length);
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

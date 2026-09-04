#if SDVKIT_GAME_AVAILABLE
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewModdingAPI.Enums;
using StardewValley;

namespace SdvKit.AlwaysOn;

public sealed class ModEntry : Mod
{
    private const int LabWindowWidth = 1280;
    private const int LabWindowHeight = 720;
    private BackgroundRunGuard? _backgroundRun;
    private StatusWriter? _statusWriter;
    private TestSaveAutomation? _testSave;
    private NetworkTwoAutomation? _networkTwo;
    private ProjectModObserver? _projectMod;
    private string? _launchId;
    private string? _stopRequestPath;
    private bool _gameLaunched;
    private bool _exitPrepared;
    private bool _labWindowModeRequired;
    private bool _labWindowModeAttempted;
    private bool _statusWriteErrorLogged;
    private bool _stopReadErrorLogged;

    public override void Entry(IModHelper helper)
    {
        if (!TryReadConfiguration(
                out string launchId,
                out string statusPath,
                out string stopRequestPath,
                out string reason))
        {
            Monitor.Log($"SDVKit AlwaysOn is inert: {reason}", LogLevel.Warn);
            return;
        }

        string networkRole =
            Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")?.Trim()
            ?? string.Empty;
        bool networkHost = string.Equals(
            networkRole,
            SdvKit.Cli.LiveLab.NetworkTwoContract.HostRole,
            StringComparison.Ordinal);
        _backgroundRun = new BackgroundRunGuard(
            new SmapiBackgroundRunState(),
            networkHost);
        _statusWriter = new StatusWriter(launchId, statusPath);
        _launchId = launchId;
        _stopRequestPath = stopRequestPath;
        _labWindowModeRequired = string.Equals(
            Environment.GetEnvironmentVariable("SDVKIT_LAB_WINDOWED")?.Trim(),
            "1",
            StringComparison.Ordinal);
        if (!ProjectModObserver.TryCreate(
                helper,
                Monitor,
                out _projectMod,
                out string projectModReason))
        {
            Monitor.Log(
                $"SDVKit project-mod observation is unavailable: {projectModReason}",
                LogLevel.Error);
        }

        if (!TestSaveAutomation.TryCreate(
                Monitor,
                WriteActiveStatus,
                networkHost,
                out _testSave,
                out string testSaveReason))
        {
            Monitor.Log(
                $"SDVKit test-save automation is unavailable: {testSaveReason}",
                LogLevel.Error);
            _testSave?.LogInitializationFailure();
        }

        if (!NetworkTwoAutomation.TryCreate(
                helper.DirectoryPath,
                Monitor,
                WriteActiveStatus,
                () => _testSave?.Snapshot,
                out _networkTwo,
                out string networkTwoReason))
        {
            Monitor.Log(
                $"SDVKit network-2 automation is unavailable: {networkTwoReason}",
                LogLevel.Error);
            _networkTwo?.LogInitializationFailure();
        }

        ReviewCommand.Register(
            helper,
            Monitor,
            Path.GetDirectoryName(statusPath)
                ?? throw new InvalidOperationException(
                    "The AlwaysOn status path has no runtime directory."),
            () => _testSave,
            () => _networkTwo);
        if (!ReviewVirtualCursor.TryInstall(out string virtualCursorError))
        {
            Monitor.Log(virtualCursorError, LogLevel.Error);
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        helper.Events.GameLoop.UpdateTicking += (_, _) =>
        {
            if (!_exitPrepared)
            {
                _backgroundRun.EnsureApplied();
                TryHandleStopRequest();
            }
        };
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) =>
        {
            if (!_exitPrepared)
            {
                // Wait through Stardew's immediate title-window initialization;
                // the bounded helper applies the lab baseline only once.
                EnsureLabWindowMode();
                // Rebind and reassert after the game's update too. During load,
                // Stardew can replace options more than once after the load-stage
                // notification and before a stable world is ready.
                _backgroundRun.RecaptureAfterOptionsReplacement();
                WriteActiveStatus();
            }
        };
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            _testSave?.OnUpdateTicked();
            _networkTwo?.OnUpdateTicked();
        };
        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            _backgroundRun!.Enable();
            _gameLaunched = true;
            _projectMod?.ObserveLoadedMod();
            WriteActiveStatus();
        };
        helper.Events.Specialized.LoadStageChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewStage == LoadStage.Preloaded)
            {
                _backgroundRun.RecaptureAfterOptionsReplacement();
            }
        };
        helper.Events.GameLoop.SaveCreating += (_, _) =>
            _testSave?.OnSaveCreating();
        helper.Events.GameLoop.SaveCreated += (_, _) =>
            _testSave?.OnSaveCreated();
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
            _testSave?.OnSaveLoaded();
        helper.Events.GameLoop.Saving += (_, _) =>
            _testSave?.OnSaving();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            ReviewVirtualCursor.Clear();
            _backgroundRun.ResetAfterReturnToTitle();
            _testSave?.OnReturnedToTitle();
            _networkTwo?.OnReturnedToTitle();
        };

        Monitor.Log(
            $"SDVKit AlwaysOn activated for isolated lab launch '{launchId}'.",
            LogLevel.Info);
    }

    private void EnsureLabWindowMode()
    {
        if (!_gameLaunched
            || !_labWindowModeRequired
            || _labWindowModeAttempted)
        {
            return;
        }

        // Startup preferences establish the deterministic 1280x720 baseline.
        // Apply and verify it once, then leave later resize and UI-scale testing alone.
        _labWindowModeAttempted = true;
        try
        {
            bool windowModeApplied = Game1.options.isCurrentlyWindowed()
                && !Game1.options.isCurrentlyWindowedBorderless()
                && Game1.options.preferredResolutionX == LabWindowWidth
                && Game1.options.preferredResolutionY == LabWindowHeight
                && Game1.game1.Window.ClientBounds.Width == LabWindowWidth
                && Game1.game1.Window.ClientBounds.Height == LabWindowHeight;
            if (windowModeApplied)
            {
                Monitor.Log(
                    "SDVKit lab confirmed Stardew's isolated windowed mode.",
                    LogLevel.Info);
                return;
            }

            bool refreshRequired =
                Game1.game1.Window.ClientBounds.Width != LabWindowWidth
                || Game1.game1.Window.ClientBounds.Height != LabWindowHeight;
            if (!Game1.options.isCurrentlyWindowed()
                || Game1.options.isCurrentlyWindowedBorderless())
            {
                Game1.options.setWindowedOption(StartupPreferences.windowed);
                refreshRequired = true;
            }

            if (Game1.options.preferredResolutionX != LabWindowWidth
                || Game1.options.preferredResolutionY != LabWindowHeight)
            {
                Game1.options.preferredResolutionX = LabWindowWidth;
                Game1.options.preferredResolutionY = LabWindowHeight;
                refreshRequired = true;
            }

            if (refreshRequired)
            {
                Game1.game1.refreshWindowSettings();
            }

            windowModeApplied = Game1.options.isCurrentlyWindowed()
                && !Game1.options.isCurrentlyWindowedBorderless()
                && Game1.options.preferredResolutionX == LabWindowWidth
                && Game1.options.preferredResolutionY == LabWindowHeight
                && Game1.game1.Window.ClientBounds.Width == LabWindowWidth
                && Game1.game1.Window.ClientBounds.Height == LabWindowHeight;
            Monitor.Log(
                windowModeApplied
                    ? "SDVKit lab confirmed Stardew's isolated windowed mode."
                    : "SDVKit lab couldn't confirm the requested initial windowed mode.",
                windowModeApplied ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            Monitor.Log(
                $"SDVKit lab couldn't apply isolated windowed mode: {exception.Message}",
                LogLevel.Error);
        }
    }

    private static bool TryReadConfiguration(
        out string launchId,
        out string statusPath,
        out string stopRequestPath,
        out string reason)
    {
        launchId = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")?.Trim()
            ?? string.Empty;
        statusPath = Environment.GetEnvironmentVariable("SDVKIT_LAB_STATUS_PATH")?.Trim()
            ?? string.Empty;
        stopRequestPath = Environment.GetEnvironmentVariable("SDVKIT_LAB_STOP_PATH")?.Trim()
            ?? string.Empty;
        string expectedDataPath =
            Environment.GetEnvironmentVariable("SDVKIT_LAB_DATA_PATH")?.Trim()
            ?? string.Empty;
        if (!Guid.TryParseExact(launchId, "N", out _))
        {
            reason = "SDVKIT_LAB_LAUNCH_ID is missing or invalid.";
            return false;
        }

        if (statusPath.Length == 0
            || stopRequestPath.Length == 0
            || expectedDataPath.Length == 0)
        {
            reason = "An SDVKit lab runtime or data path is missing or empty.";
            return false;
        }

        if (!Path.IsPathFullyQualified(statusPath)
            || !Path.IsPathFullyQualified(stopRequestPath)
            || !Path.IsPathFullyQualified(expectedDataPath))
        {
            reason = "The lab runtime and data paths must be fully qualified.";
            return false;
        }

        try
        {
            statusPath = Path.GetFullPath(statusPath);
            stopRequestPath = Path.GetFullPath(stopRequestPath);
            expectedDataPath = Path.GetFullPath(expectedDataPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            reason = $"A lab runtime path is invalid: {exception.Message}";
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetDirectoryName(statusPath),
                Path.GetDirectoryName(stopRequestPath),
                comparison))
        {
            reason = "The lab status and stop-request paths must share one runtime directory.";
            return false;
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Constants.DataPath),
                Path.TrimEndingDirectorySeparator(expectedDataPath),
                comparison))
        {
            reason = $"Stardew resolved an unexpected data path: {Constants.DataPath}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void TryHandleStopRequest()
    {
        if (!_gameLaunched
            || _exitPrepared
            || _testSave is { CanStop: false }
            || _networkTwo is { CanStop: false })
        {
            return;
        }

        string requestedLaunchId;
        try
        {
            if (!File.Exists(_stopRequestPath))
            {
                return;
            }

            requestedLaunchId = File.ReadAllText(_stopRequestPath).Trim();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            if (!_stopReadErrorLogged)
            {
                Monitor.Log(
                    $"SDVKit AlwaysOn couldn't read its stop request: {exception.Message}",
                    LogLevel.Error);
                _stopReadErrorLogged = true;
            }

            return;
        }

        if (!string.Equals(requestedLaunchId, _launchId, StringComparison.Ordinal))
        {
            if (!_stopReadErrorLogged)
            {
                Monitor.Log(
                    "SDVKit AlwaysOn ignored a stop request for another launch.",
                    LogLevel.Error);
                _stopReadErrorLogged = true;
            }

            return;
        }

        if (PrepareForExit())
        {
            GameRunner.instance.Exit();
        }
    }

    private bool PrepareForExit()
    {
        if (_exitPrepared)
        {
            return true;
        }

        int tick = Game1.ticks;
        ReviewVirtualCursor.Clear();
        WindowsForegroundWindowObservation? foregroundWindow =
            _networkTwo?.ForegroundWindow;
        bool isActive = GetReportedIsActive(foregroundWindow);
        BackgroundRunRestoreResult restore;
        try
        {
            restore = _backgroundRun!.RestoreOriginalAndDisable();
        }
        catch (Exception exception)
        {
            restore = new BackgroundRunRestoreResult(false, null);
            Monitor.Log(
                $"SDVKit AlwaysOn couldn't restore the background-run option: {exception.Message}",
                LogLevel.Error);
        }

        // These options belong to the isolated .sdvkit profile. A failed readback
        // remains visible in the terminal marker, but it must not hold the exact
        // lab process open; the next launch applies the lab values again.
        _exitPrepared = true;

        WriteStatus(
            restore.Succeeded ? "exiting" : "restoreFailed",
            tick,
            isActive,
            restore.ConfirmedPauseWhenOutOfFocus,
            restore.ConfirmedEnableServer,
            restore.ConfirmedIpConnectionsEnabled,
            foregroundWindow?.WindowHandle,
            foregroundWindow?.ProcessId);
        return true;
    }

    private void WriteActiveStatus()
    {
        bool networkHost = _networkTwo?.IsHost == true;
        WindowsForegroundWindowObservation? foregroundWindow =
            _networkTwo is null
                ? WindowsForegroundWindowProbe.Observe()
                : _networkTwo.ForegroundWindow;
        WriteStatus(
            "active",
            Game1.ticks,
            GetReportedIsActive(foregroundWindow),
            Game1.options.pauseWhenOutOfFocus,
            networkHost ? Game1.options.enableServer : null,
            networkHost ? Game1.options.ipConnectionsEnabled : null,
            foregroundWindow?.WindowHandle,
            foregroundWindow?.ProcessId);
    }

    private static bool GetReportedIsActive(
        WindowsForegroundWindowObservation? foregroundWindow) =>
        foregroundWindow is null
            ? GameRunner.instance.IsActive
            : foregroundWindow.Value.IsCurrentProcess ?? true;

    private void WriteStatus(
        string phase,
        int tick,
        bool isActive,
        bool? pauseWhenOutOfFocus,
        bool? enableServer,
        bool? ipConnectionsEnabled,
        long? foregroundWindowHandle,
        int? foregroundProcessId)
    {
        try
        {
            _statusWriter!.Write(
                phase,
                tick,
                isActive,
                pauseWhenOutOfFocus,
                _testSave?.Snapshot,
                enableServer,
                ipConnectionsEnabled,
                _networkTwo?.Snapshot,
                foregroundWindowHandle,
                foregroundProcessId,
                _projectMod?.Snapshot,
                CaptureRuntimeSnapshot(),
                _projectMod?.LoadedModsSnapshot);
            _statusWriteErrorLogged = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_statusWriteErrorLogged)
            {
                Monitor.Log(
                    $"SDVKit AlwaysOn couldn't write its lab status marker: {exception.Message}",
                    LogLevel.Error);
                _statusWriteErrorLogged = true;
            }
        }
    }

    private static RuntimeSnapshotMarker CaptureRuntimeSnapshot()
    {
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        bool worldReady = Context.IsWorldReady;
        if (!worldReady)
        {
            return new RuntimeSnapshotMarker(
                RuntimeSnapshotContract.SchemaVersion,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                Game1.activeClickableMenu is not null,
                observedAtUtc);
        }

        return new RuntimeSnapshotMarker(
            RuntimeSnapshotContract.SchemaVersion,
            true,
            Game1.currentSeason,
            Game1.dayOfMonth,
            Game1.year,
            Game1.timeOfDay,
            Game1.player.currentLocation.NameOrUniqueName,
            Game1.player.TilePoint.X,
            Game1.player.TilePoint.Y,
            Game1.activeClickableMenu is not null,
            observedAtUtc);
    }

    private void OnProcessExit(object? sender, EventArgs eventArgs)
    {
        // ProcessExit is only an unconfirmed best-effort restoration fallback;
        // the controlled stop request is the sole reported normal-exit path.
        try
        {
            if (!_exitPrepared)
            {
                _backgroundRun?.RestoreOriginalAndDisable();
            }
        }
        catch
        {
            // Process teardown can't safely report or recover a restoration failure.
        }
    }
}
#endif

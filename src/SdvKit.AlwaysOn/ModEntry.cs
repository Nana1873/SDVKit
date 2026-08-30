#if SDVKIT_GAME_AVAILABLE
using StardewModdingAPI;
using StardewModdingAPI.Enums;
using StardewValley;

namespace SdvKit.AlwaysOn;

public sealed class ModEntry : Mod
{
    private BackgroundRunGuard? _backgroundRun;
    private StatusWriter? _statusWriter;
    private string? _launchId;
    private string? _stopRequestPath;
    private bool _gameLaunched;
    private bool _exitPrepared;
    private bool _restoreConfirmed;
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

        _backgroundRun = new BackgroundRunGuard(new SmapiBackgroundRunState());
        _statusWriter = new StatusWriter(launchId, statusPath);
        _launchId = launchId;
        _stopRequestPath = stopRequestPath;
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
                WriteActiveStatus();
            }
        };
        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            _backgroundRun.Enable();
            _gameLaunched = true;
            GameRunner.instance.Exiting += (_, _) => PrepareForExit();
            WriteActiveStatus();
        };
        helper.Events.Specialized.LoadStageChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewStage == LoadStage.Preloaded)
            {
                _backgroundRun.RecaptureAfterOptionsReplacement();
            }
        };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
            _backgroundRun.ResetAfterReturnToTitle();

        Monitor.Log(
            $"SDVKit AlwaysOn activated for isolated lab launch '{launchId}'.",
            LogLevel.Info);
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
        if (!Guid.TryParseExact(launchId, "N", out _))
        {
            reason = "SDVKIT_LAB_LAUNCH_ID is missing or invalid.";
            return false;
        }

        if (statusPath.Length == 0 || stopRequestPath.Length == 0)
        {
            reason = "SDVKIT_LAB_STATUS_PATH or SDVKIT_LAB_STOP_PATH is missing or empty.";
            return false;
        }

        if (!Path.IsPathFullyQualified(statusPath)
            || !Path.IsPathFullyQualified(stopRequestPath))
        {
            reason = "The lab status and stop-request paths must be fully qualified.";
            return false;
        }

        try
        {
            statusPath = Path.GetFullPath(statusPath);
            stopRequestPath = Path.GetFullPath(stopRequestPath);
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

        reason = string.Empty;
        return true;
    }

    private void TryHandleStopRequest()
    {
        if (!_gameLaunched || _exitPrepared)
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
            return _restoreConfirmed;
        }

        int tick = Game1.ticks;
        bool isActive = GameRunner.instance.IsActive;
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

        _exitPrepared = true;
        _restoreConfirmed = restore.Succeeded;
        WriteStatus(
            restore.Succeeded ? "exiting" : "restoreFailed",
            tick,
            isActive,
            restore.ConfirmedPauseWhenOutOfFocus);
        return _restoreConfirmed;
    }

    private void WriteActiveStatus()
    {
        WriteStatus(
            "active",
            Game1.ticks,
            GameRunner.instance.IsActive,
            Game1.options.pauseWhenOutOfFocus);
    }

    private void WriteStatus(
        string phase,
        int tick,
        bool isActive,
        bool? pauseWhenOutOfFocus)
    {
        try
        {
            _statusWriter!.Write(
                phase,
                tick,
                isActive,
                pauseWhenOutOfFocus);
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

    private void OnProcessExit(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (!_exitPrepared)
            {
                _backgroundRun?.RestoreOriginalAndDisable();
            }
        }
        catch
        {
            // ProcessExit is only an idempotent best-effort fallback to GameRunner.Exiting.
        }
    }
}
#endif

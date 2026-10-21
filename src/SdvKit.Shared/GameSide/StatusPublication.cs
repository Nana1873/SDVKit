namespace SdvKit.GameSide;

internal sealed class StatusPublication
{
    private bool _activeWriteErrorLogged;
    private bool _fallbackLogged;
    private bool _asideLeftoverLogged;

    public bool TryWrite(
        string phase,
        Action write,
        Action<string> logError,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null)
    {
        try
        {
            write();
            _activeWriteErrorLogged = false;
            if (!_fallbackLogged && WindowsStatusFile.UsedHeldDestinationFallback)
            {
                _fallbackLogged = true;
                logInfo?.Invoke("SDVKit status snapshot replaced through the held-destination fallback; see #136.");
            }

            // Like the write error above, a leftover is reported once per period; a publication
            // without a leftover re-arms the report.
            string? leftover = WindowsStatusFile.AsideCleanupError;
            if (leftover is null)
            {
                _asideLeftoverLogged = false;
            }
            else if (!_asideLeftoverLogged)
            {
                _asideLeftoverLogged = true;
                logWarn?.Invoke($"SDVKit lab status: {leftover}");
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (phase is "exiting" or "restoreFailed")
            {
                logError($"SDVKit couldn't publish its final '{phase}' lab status marker: {exception.Message} "
                    + "The game will still exit, but this update cannot confirm the final lab status.");
            }
            else if (!_activeWriteErrorLogged)
            {
                logError($"SDVKit couldn't write its lab status marker: {exception.Message}");
                _activeWriteErrorLogged = true;
            }

            return false;
        }
    }
}

namespace SdvKit.AlwaysOn;

internal sealed class StatusPublication
{
    private bool _activeWriteErrorLogged;

    public bool TryWrite(string phase, Action write, Action<string> logError)
    {
        try
        {
            write();
            _activeWriteErrorLogged = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (phase is "exiting" or "restoreFailed")
            {
                logError($"SDVKit AlwaysOn couldn't publish its final '{phase}' lab status marker: {exception.Message} "
                    + "The game will still exit, but this update cannot confirm the final lab status.");
            }
            else if (!_activeWriteErrorLogged)
            {
                logError($"SDVKit AlwaysOn couldn't write its lab status marker: {exception.Message}");
                _activeWriteErrorLogged = true;
            }

            return false;
        }
    }
}

namespace SdvKit.AlwaysOn;

internal interface IBackgroundRunState
{
    bool IsAvailable { get; }

    object? OptionsIdentity { get; }

    bool PauseWhenOutOfFocus { get; set; }

    bool EnableServer { get; set; }

    bool IpConnectionsEnabled { get; set; }
}

internal readonly record struct BackgroundRunRestoreResult(
    bool Succeeded,
    bool? ConfirmedPauseWhenOutOfFocus,
    bool? ConfirmedEnableServer = null,
    bool? ConfirmedIpConnectionsEnabled = null);

internal sealed class BackgroundRunGuard
{
    private readonly IBackgroundRunState _state;
    private readonly bool _networkHost;
    private bool _enabled;
    private bool _originalCaptured;
    private bool _originalPauseWhenOutOfFocus;
    private bool _originalEnableServer;
    private bool _originalIpConnectionsEnabled;
    private object? _capturedOptionsIdentity;
    private bool _recapturePending;

    public BackgroundRunGuard(IBackgroundRunState state, bool networkHost = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _networkHost = networkHost;
    }

    public void Enable()
    {
        _enabled = true;
        EnsureApplied();
    }

    public void EnsureApplied()
    {
        if (!_enabled || !_state.IsAvailable)
        {
            return;
        }

        if (_recapturePending)
        {
            if (!_originalCaptured || !CurrentOptionsAreCaptured())
            {
                CaptureCurrentOptions();
                _recapturePending = false;
            }
        }
        else if (!_originalCaptured)
        {
            CaptureCurrentOptions();
        }
        else if (!CurrentOptionsAreCaptured())
        {
            return;
        }

        Apply(
            pauseWhenOutOfFocus: false,
            enableServer: true,
            ipConnectionsEnabled: true);
    }

    public void RecaptureAfterOptionsReplacement()
    {
        _recapturePending = true;
        EnsureApplied();
    }

    public void ResetAfterReturnToTitle()
    {
        RecaptureAfterOptionsReplacement();
    }

    public BackgroundRunRestoreResult RestoreOriginalAndDisable()
    {
        _enabled = false;
        _recapturePending = false;
        var restored = false;
        try
        {
            if (!_originalCaptured
                || !_state.IsAvailable
                || !CurrentOptionsAreCaptured())
            {
                return new BackgroundRunRestoreResult(false, null);
            }

            Apply(
                _originalPauseWhenOutOfFocus,
                _originalEnableServer,
                _originalIpConnectionsEnabled);
            if (!_state.IsAvailable || !CurrentOptionsAreCaptured())
            {
                return new BackgroundRunRestoreResult(false, null);
            }

            bool currentPauseWhenOutOfFocus = _state.PauseWhenOutOfFocus;
            bool? currentEnableServer = _networkHost ? _state.EnableServer : null;
            bool? currentIpConnectionsEnabled = _networkHost
                ? _state.IpConnectionsEnabled
                : null;
            var result = new BackgroundRunRestoreResult(
                currentPauseWhenOutOfFocus == _originalPauseWhenOutOfFocus
                    && (!_networkHost
                        || currentEnableServer == _originalEnableServer
                        && currentIpConnectionsEnabled == _originalIpConnectionsEnabled),
                currentPauseWhenOutOfFocus,
                currentEnableServer,
                currentIpConnectionsEnabled);
            restored = result.Succeeded;
            return result;
        }
        finally
        {
            if (restored)
            {
                _originalCaptured = false;
                _originalPauseWhenOutOfFocus = false;
                _originalEnableServer = false;
                _originalIpConnectionsEnabled = false;
                _capturedOptionsIdentity = null;
            }
            else
            {
                // A clean-stop retry may observe the same temporarily detached
                // options instance. Preserve its true original; if Stardew
                // replaces the instance instead, the pending recapture binds
                // that replacement before the guard writes it.
                _recapturePending = true;
            }
        }
    }

    private void Apply(
        bool pauseWhenOutOfFocus,
        bool enableServer,
        bool ipConnectionsEnabled)
    {
        if (_state.PauseWhenOutOfFocus != pauseWhenOutOfFocus)
        {
            _state.PauseWhenOutOfFocus = pauseWhenOutOfFocus;
        }

        if (!_networkHost)
        {
            return;
        }

        if (_state.EnableServer != enableServer)
        {
            _state.EnableServer = enableServer;
        }

        if (_state.IpConnectionsEnabled != ipConnectionsEnabled)
        {
            _state.IpConnectionsEnabled = ipConnectionsEnabled;
        }
    }

    private void CaptureCurrentOptions()
    {
        object? identity = _state.OptionsIdentity;
        if (identity is null)
        {
            throw new InvalidOperationException(
                "Background-run options are available without a stable instance identity.");
        }

        _originalPauseWhenOutOfFocus = _state.PauseWhenOutOfFocus;
        if (_networkHost)
        {
            _originalEnableServer = _state.EnableServer;
            _originalIpConnectionsEnabled = _state.IpConnectionsEnabled;
        }

        _capturedOptionsIdentity = identity;
        _originalCaptured = true;
    }

    private bool CurrentOptionsAreCaptured() =>
        ReferenceEquals(_capturedOptionsIdentity, _state.OptionsIdentity);
}

#if SDVKIT_GAME_AVAILABLE
internal sealed class SmapiBackgroundRunState : IBackgroundRunState
{
    public bool IsAvailable => StardewValley.Game1.options is not null;

    public object? OptionsIdentity => StardewValley.Game1.options;

    public bool PauseWhenOutOfFocus
    {
        get => StardewValley.Game1.options.pauseWhenOutOfFocus;
        set => StardewValley.Game1.options.pauseWhenOutOfFocus = value;
    }

    public bool EnableServer
    {
        get => StardewValley.Game1.options.enableServer;
        set => StardewValley.Game1.options.enableServer = value;
    }

    public bool IpConnectionsEnabled
    {
        get => StardewValley.Game1.options.ipConnectionsEnabled;
        set => StardewValley.Game1.options.ipConnectionsEnabled = value;
    }
}
#endif

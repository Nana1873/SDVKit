namespace SdvKit.AlwaysOn;

internal interface IBackgroundRunState
{
    bool IsAvailable { get; }

    object? OptionsIdentity { get; }

    bool PauseWhenOutOfFocus { get; set; }
}

internal readonly record struct BackgroundRunRestoreResult(
    bool Succeeded,
    bool? ConfirmedPauseWhenOutOfFocus);

internal sealed class BackgroundRunGuard
{
    private readonly IBackgroundRunState _state;
    private bool _enabled;
    private bool _originalCaptured;
    private bool _originalPauseWhenOutOfFocus;
    private object? _capturedOptionsIdentity;
    private bool _recapturePending;

    public BackgroundRunGuard(IBackgroundRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
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
            }

            _recapturePending = false;
        }
        else if (!_originalCaptured)
        {
            CaptureCurrentOptions();
        }
        else if (!CurrentOptionsAreCaptured())
        {
            return;
        }

        Apply(pauseWhenOutOfFocus: false);
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
        try
        {
            if (!_originalCaptured
                || !_state.IsAvailable
                || !CurrentOptionsAreCaptured())
            {
                return new BackgroundRunRestoreResult(false, null);
            }

            Apply(_originalPauseWhenOutOfFocus);
            if (!_state.IsAvailable || !CurrentOptionsAreCaptured())
            {
                return new BackgroundRunRestoreResult(false, null);
            }

            bool currentPauseWhenOutOfFocus = _state.PauseWhenOutOfFocus;
            return new BackgroundRunRestoreResult(
                currentPauseWhenOutOfFocus == _originalPauseWhenOutOfFocus,
                currentPauseWhenOutOfFocus);
        }
        finally
        {
            _originalCaptured = false;
            _originalPauseWhenOutOfFocus = false;
            _capturedOptionsIdentity = null;
        }
    }

    private void Apply(bool pauseWhenOutOfFocus)
    {
        if (_state.PauseWhenOutOfFocus != pauseWhenOutOfFocus)
        {
            _state.PauseWhenOutOfFocus = pauseWhenOutOfFocus;
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
}
#endif

using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class BackgroundRunGuardTests
{
    [Fact]
    public void EnableCapturesOriginalAndAppliesFalse()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);

        guard.Enable();
        guard.EnsureApplied();

        Assert.False(state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.Equal(1, state.TotalSetCount);
    }

    [Fact]
    public void EnabledGuardReassertsFalseAfterGameChangesCurrentOptions()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();

        state.ResetCurrentByGame(pauseWhenOutOfFocus: true);
        guard.EnsureApplied();

        Assert.False(state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.Equal(2, state.TotalSetCount);
    }

    [Fact]
    public void PendingPreloadedRecaptureCompletesWhenReplacementBecomesAvailable()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        state.DetachOptions();

        guard.RecaptureAfterOptionsReplacement();
        FakeOptions replacement = state.ReplaceOptions(pauseWhenOutOfFocus: true);
        guard.EnsureApplied();

        Assert.False(replacement.PauseWhenOutOfFocus);
        Assert.Equal(1, replacement.SetCount);

        guard.RestoreOriginalAndDisable();

        Assert.True(replacement.PauseWhenOutOfFocus);
        Assert.Equal(2, replacement.SetCount);
    }

    [Fact]
    public void PendingPreloadedForSameIdentityDoesNotCaptureTemporaryFalse()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        FakeOptions sameOptions = state.DetachOptions();

        guard.RecaptureAfterOptionsReplacement();
        state.AttachOptions(sameOptions);
        guard.EnsureApplied();
        guard.RestoreOriginalAndDisable();

        Assert.True(sameOptions.PauseWhenOutOfFocus);
        Assert.Equal(2, sameOptions.SetCount);
    }

    [Fact]
    public void PreloadedWatchRebindsWhenReplacementArrivesAfterTheEventCallback()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();

        guard.RecaptureAfterOptionsReplacement();
        FakeOptions replacement = state.ReplaceOptions(pauseWhenOutOfFocus: true);
        guard.EnsureApplied();
        Assert.False(replacement.PauseWhenOutOfFocus);

        BackgroundRunRestoreResult restored = guard.RestoreOriginalAndDisable();

        Assert.True(restored.Succeeded);
        Assert.True(restored.ConfirmedPauseWhenOutOfFocus);
        Assert.True(replacement.PauseWhenOutOfFocus);
        Assert.Equal(2, replacement.SetCount);
    }

    [Fact]
    public void PeriodicWatchRebindsASecondReplacementDuringLoad()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();

        guard.RecaptureAfterOptionsReplacement();
        state.ReplaceOptions(pauseWhenOutOfFocus: true);
        guard.EnsureApplied();
        FakeOptions secondReplacement = state.ReplaceOptions(pauseWhenOutOfFocus: true);

        guard.EnsureApplied();

        Assert.True(secondReplacement.PauseWhenOutOfFocus);
        Assert.Equal(0, secondReplacement.SetCount);

        guard.RecaptureAfterOptionsReplacement();

        Assert.False(secondReplacement.PauseWhenOutOfFocus);
        BackgroundRunRestoreResult restored = guard.RestoreOriginalAndDisable();
        Assert.True(restored.Succeeded);
        Assert.True(restored.ConfirmedPauseWhenOutOfFocus);
        Assert.True(secondReplacement.PauseWhenOutOfFocus);
        Assert.Equal(2, secondReplacement.SetCount);
    }

    [Fact]
    public void RepeatedPreloadedForSameOptionsDoesNotCaptureTemporaryFalse()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();

        guard.RecaptureAfterOptionsReplacement();
        guard.RecaptureAfterOptionsReplacement();
        guard.RestoreOriginalAndDisable();

        Assert.True(state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.Equal(2, state.TotalSetCount);
    }

    [Fact]
    public void ResetAfterReturnToTitleRecapturesAReplacementSafely()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        FakeOptions replacement = state.ReplaceOptions(pauseWhenOutOfFocus: true);

        guard.ResetAfterReturnToTitle();

        Assert.False(replacement.PauseWhenOutOfFocus);
        Assert.Equal(1, replacement.SetCount);

        guard.RestoreOriginalAndDisable();

        Assert.True(replacement.PauseWhenOutOfFocus);
        Assert.Equal(2, replacement.SetCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoreReturnsTheConfirmedOriginalAfterApplyAndReadback(bool original)
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: original);
        BackgroundRunGuard guard = new(state);
        guard.Enable();

        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();

        Assert.True(result.Succeeded);
        Assert.Equal(original, result.ConfirmedPauseWhenOutOfFocus);
        Assert.Equal(original, state.CurrentOptions.PauseWhenOutOfFocus);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void NetworkHostForcesAndRestoresEveryOriginalOptionCombination(
        bool pauseWhenOutOfFocus,
        bool enableServer,
        bool ipConnectionsEnabled)
    {
        FakeBackgroundRunState state = new(
            pauseWhenOutOfFocus,
            enableServer,
            ipConnectionsEnabled);
        BackgroundRunGuard guard = new(state, networkHost: true);

        guard.Enable();

        Assert.False(state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.True(state.CurrentOptions.EnableServer);
        Assert.True(state.CurrentOptions.IpConnectionsEnabled);

        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();

        Assert.True(result.Succeeded);
        Assert.Equal(pauseWhenOutOfFocus, result.ConfirmedPauseWhenOutOfFocus);
        Assert.Equal(enableServer, result.ConfirmedEnableServer);
        Assert.Equal(ipConnectionsEnabled, result.ConfirmedIpConnectionsEnabled);
        Assert.Equal(pauseWhenOutOfFocus, state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.Equal(enableServer, state.CurrentOptions.EnableServer);
        Assert.Equal(ipConnectionsEnabled, state.CurrentOptions.IpConnectionsEnabled);
        Assert.Equal(pauseWhenOutOfFocus ? 2 : 0, state.CurrentOptions.SetCount);
        Assert.Equal(enableServer ? 0 : 2, state.CurrentOptions.EnableServerSetCount);
        Assert.Equal(
            ipConnectionsEnabled ? 0 : 2,
            state.CurrentOptions.IpConnectionsEnabledSetCount);
    }

    [Fact]
    public void NetworkHostRecapturesAllOptionsFromAReplacement()
    {
        FakeBackgroundRunState state = new(
            pauseWhenOutOfFocus: false,
            enableServer: true,
            ipConnectionsEnabled: true);
        BackgroundRunGuard guard = new(state, networkHost: true);
        guard.Enable();

        guard.RecaptureAfterOptionsReplacement();
        FakeOptions replacement = state.ReplaceOptions(
            pauseWhenOutOfFocus: true,
            enableServer: false,
            ipConnectionsEnabled: false);
        guard.EnsureApplied();

        Assert.False(replacement.PauseWhenOutOfFocus);
        Assert.True(replacement.EnableServer);
        Assert.True(replacement.IpConnectionsEnabled);

        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();

        Assert.True(result.Succeeded);
        Assert.True(result.ConfirmedPauseWhenOutOfFocus);
        Assert.False(result.ConfirmedEnableServer);
        Assert.False(result.ConfirmedIpConnectionsEnabled);
        Assert.True(replacement.PauseWhenOutOfFocus);
        Assert.False(replacement.EnableServer);
        Assert.False(replacement.IpConnectionsEnabled);
        Assert.Equal(2, replacement.SetCount);
        Assert.Equal(2, replacement.EnableServerSetCount);
        Assert.Equal(2, replacement.IpConnectionsEnabledSetCount);
    }

    [Fact]
    public void NetworkHostReassertsNetworkOptionsAfterGameChangesCurrentOptions()
    {
        FakeBackgroundRunState state = new(
            pauseWhenOutOfFocus: true,
            enableServer: false,
            ipConnectionsEnabled: false);
        BackgroundRunGuard guard = new(state, networkHost: true);
        guard.Enable();

        state.ResetCurrentNetworkByGame(
            enableServer: false,
            ipConnectionsEnabled: false);
        guard.EnsureApplied();

        Assert.False(state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.True(state.CurrentOptions.EnableServer);
        Assert.True(state.CurrentOptions.IpConnectionsEnabled);
        Assert.Equal(2, state.CurrentOptions.EnableServerSetCount);
        Assert.Equal(2, state.CurrentOptions.IpConnectionsEnabledSetCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NonHostNeverReadsOrWritesNetworkOptions(
        bool enableServer,
        bool ipConnectionsEnabled)
    {
        FakeBackgroundRunState state = new(
            pauseWhenOutOfFocus: true,
            enableServer,
            ipConnectionsEnabled);
        BackgroundRunGuard guard = new(state);

        guard.Enable();
        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();

        Assert.True(result.Succeeded);
        Assert.True(result.ConfirmedPauseWhenOutOfFocus);
        Assert.Null(result.ConfirmedEnableServer);
        Assert.Null(result.ConfirmedIpConnectionsEnabled);
        Assert.Equal(0, state.EnableServerGetCount);
        Assert.Equal(0, state.IpConnectionsEnabledGetCount);
        Assert.Equal(enableServer, state.CurrentOptions.EnableServer);
        Assert.Equal(ipConnectionsEnabled, state.CurrentOptions.IpConnectionsEnabled);
        Assert.Equal(0, state.CurrentOptions.EnableServerSetCount);
        Assert.Equal(0, state.CurrentOptions.IpConnectionsEnabledSetCount);
    }

    [Fact]
    public void NetworkHostRestoreWhileOptionsAreUnavailableReportsEveryOptionUnconfirmed()
    {
        FakeBackgroundRunState state = new(
            pauseWhenOutOfFocus: true,
            enableServer: false,
            ipConnectionsEnabled: false);
        BackgroundRunGuard guard = new(state, networkHost: true);
        guard.Enable();
        state.DetachOptions();

        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();

        Assert.False(result.Succeeded);
        Assert.Null(result.ConfirmedPauseWhenOutOfFocus);
        Assert.Null(result.ConfirmedEnableServer);
        Assert.Null(result.ConfirmedIpConnectionsEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NetworkHostFailedReadbackPreservesOriginalsForCleanStopRetry(
        bool rejectEnableServer)
    {
        FakeBackgroundRunState state = new(
            pauseWhenOutOfFocus: true,
            enableServer: false,
            ipConnectionsEnabled: false);
        BackgroundRunGuard guard = new(state, networkHost: true);
        guard.Enable();

        state.CurrentOptions.RejectEnableServerWrites =
            rejectEnableServer;
        state.CurrentOptions.RejectIpConnectionsEnabledWrites =
            !rejectEnableServer;

        BackgroundRunRestoreResult failed = guard.RestoreOriginalAndDisable();

        Assert.False(failed.Succeeded);
        Assert.True(failed.ConfirmedPauseWhenOutOfFocus);
        Assert.Equal(
            rejectEnableServer,
            failed.ConfirmedEnableServer);
        Assert.Equal(
            !rejectEnableServer,
            failed.ConfirmedIpConnectionsEnabled);

        state.CurrentOptions.RejectEnableServerWrites = false;
        state.CurrentOptions.RejectIpConnectionsEnabledWrites = false;
        guard.Enable();
        BackgroundRunRestoreResult retry = guard.RestoreOriginalAndDisable();

        Assert.True(retry.Succeeded);
        Assert.True(retry.ConfirmedPauseWhenOutOfFocus);
        Assert.False(retry.ConfirmedEnableServer);
        Assert.False(retry.ConfirmedIpConnectionsEnabled);
        Assert.True(state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.False(state.CurrentOptions.EnableServer);
        Assert.False(state.CurrentOptions.IpConnectionsEnabled);
    }

    [Fact]
    public void RestoreWhileOptionsAreUnavailableReportsUnconfirmedAndStaysDisabled()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        FakeOptions captured = state.DetachOptions();

        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();
        state.AttachOptions(captured);
        guard.EnsureApplied();

        Assert.False(result.Succeeded);
        Assert.Null(result.ConfirmedPauseWhenOutOfFocus);
        Assert.False(captured.PauseWhenOutOfFocus);
        Assert.Equal(1, captured.SetCount);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void RestoreOriginalAndDisableIsIdempotent(bool original, int expectedSetCount)
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: original);
        BackgroundRunGuard guard = new(state);
        guard.Enable();

        guard.RestoreOriginalAndDisable();
        guard.EnsureApplied();
        guard.RestoreOriginalAndDisable();

        Assert.Equal(original, state.CurrentOptions.PauseWhenOutOfFocus);
        Assert.Equal(expectedSetCount, state.TotalSetCount);
    }

    [Fact]
    public void ForeignReplacementIsNeverWrittenAndRestoreFailsClosed()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        FakeOptions foreign = state.ReplaceOptions(pauseWhenOutOfFocus: true);

        guard.EnsureApplied();
        BackgroundRunRestoreResult result = guard.RestoreOriginalAndDisable();
        guard.EnsureApplied();

        Assert.False(result.Succeeded);
        Assert.Null(result.ConfirmedPauseWhenOutOfFocus);
        Assert.True(foreign.PauseWhenOutOfFocus);
        Assert.Equal(0, foreign.SetCount);
    }

    [Fact]
    public void FailedRestoreCanRebindAReplacementAndRetryTheSameCleanStop()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        FakeOptions replacement = state.ReplaceOptions(pauseWhenOutOfFocus: true);

        BackgroundRunRestoreResult first = guard.RestoreOriginalAndDisable();
        guard.Enable();
        BackgroundRunRestoreResult retry = guard.RestoreOriginalAndDisable();

        Assert.False(first.Succeeded);
        Assert.True(retry.Succeeded);
        Assert.True(retry.ConfirmedPauseWhenOutOfFocus);
        Assert.True(replacement.PauseWhenOutOfFocus);
        Assert.Equal(2, replacement.SetCount);
    }

    [Fact]
    public void FailedRestoreRetainsTheOriginalForTheSameReattachedOptions()
    {
        FakeBackgroundRunState state = new(pauseWhenOutOfFocus: true);
        BackgroundRunGuard guard = new(state);
        guard.Enable();
        FakeOptions originalOptions = state.DetachOptions();

        BackgroundRunRestoreResult first = guard.RestoreOriginalAndDisable();
        guard.Enable();
        state.AttachOptions(originalOptions);
        guard.EnsureApplied();
        BackgroundRunRestoreResult retry = guard.RestoreOriginalAndDisable();

        Assert.False(first.Succeeded);
        Assert.True(retry.Succeeded);
        Assert.True(retry.ConfirmedPauseWhenOutOfFocus);
        Assert.True(originalOptions.PauseWhenOutOfFocus);
        Assert.Equal(2, originalOptions.SetCount);
    }

    [Fact]
    public void ControlledStopAttemptsRestoreBeforeItsMarkerWithoutGatingNormalExit()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SdvKit.AlwaysOn",
            "ModEntry.cs"))
            .ReplaceLineEndings("\n");

        int controlledStop = source.IndexOf(
            "if (PrepareForExit())",
            StringComparison.Ordinal);
        int preparation = source.IndexOf(
            "private bool PrepareForExit()",
            controlledStop,
            StringComparison.Ordinal);
        int restore = source.IndexOf(
            "_backgroundRun!.RestoreOriginalAndDisable();",
            preparation,
            StringComparison.Ordinal);
        int exitPrepared = source.IndexOf(
            "_exitPrepared = true;",
            restore,
            StringComparison.Ordinal);
        int exitingMarker = source.IndexOf(
            "restore.Succeeded ? \"exiting\" : \"restoreFailed\"",
            restore,
            StringComparison.Ordinal);
        int markerWrite = source.IndexOf(
            "WriteStatus(",
            restore,
            StringComparison.Ordinal);
        int unconditionalExit = source.IndexOf(
            "return true;",
            markerWrite,
            StringComparison.Ordinal);

        Assert.True(controlledStop >= 0);
        Assert.True(preparation > controlledStop);
        Assert.True(restore > preparation);
        Assert.True(exitPrepared > restore);
        Assert.True(exitingMarker > restore);
        Assert.True(markerWrite > restore);
        Assert.True(unconditionalExit > markerWrite);
        Assert.DoesNotContain("GameRunner.instance.Exiting", source, StringComparison.Ordinal);
        Assert.Contains(
            "the controlled stop request is the sole reported normal-exit path.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewWindowSizeIsOnlyAppliedOnceAtStartup()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SdvKit.AlwaysOn",
            "ModEntry.cs"))
            .ReplaceLineEndings("\n");

        int method = source.IndexOf(
            "private void EnsureReviewWindowMode()",
            StringComparison.Ordinal);
        int attemptedGuard = source.IndexOf(
            "|| _reviewWindowModeAttempted)",
            method,
            StringComparison.Ordinal);
        int attemptLatch = source.IndexOf(
            "_reviewWindowModeAttempted = true;",
            method,
            StringComparison.Ordinal);
        int sizeInspection = source.IndexOf(
            "Game1.game1.Window.ClientBounds.Width",
            method,
            StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(attemptedGuard > method);
        Assert.True(attemptLatch > attemptedGuard);
        Assert.True(sizeInspection > attemptLatch);
        Assert.Contains(
            "Apply and verify it once, then leave later resize and UI-scale testing alone.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FirstOneSecondUpdateAppliesWindowThenRebindsBackgroundRun()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SdvKit.AlwaysOn",
            "ModEntry.cs"))
            .ReplaceLineEndings("\n");

        int handler = source.IndexOf(
            "helper.Events.GameLoop.OneSecondUpdateTicked +=",
            StringComparison.Ordinal);
        int windowBaseline = source.IndexOf(
            "EnsureReviewWindowMode();",
            handler,
            StringComparison.Ordinal);
        int reassert = source.IndexOf(
            "_backgroundRun.RecaptureAfterOptionsReplacement();",
            handler,
            StringComparison.Ordinal);
        int status = source.IndexOf(
            "WriteActiveStatus();",
            handler,
            StringComparison.Ordinal);

        Assert.True(handler >= 0);
        Assert.True(windowBaseline > handler);
        Assert.True(reassert > windowBaseline);
        Assert.True(status > reassert);
        int immediateHandler = source.IndexOf(
            "helper.Events.GameLoop.UpdateTicking +=",
            StringComparison.Ordinal);
        Assert.True(immediateHandler >= 0);
        Assert.DoesNotContain(
            "EnsureReviewWindowMode();",
            source[immediateHandler..handler],
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SDVKit.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }

    private sealed class FakeBackgroundRunState : IBackgroundRunState
    {
        private FakeOptions? _currentOptions;

        public FakeBackgroundRunState(
            bool pauseWhenOutOfFocus,
            bool enableServer = false,
            bool ipConnectionsEnabled = false)
        {
            ReplaceOptions(
                pauseWhenOutOfFocus,
                enableServer,
                ipConnectionsEnabled);
        }

        public bool IsAvailable => _currentOptions is not null;

        public object? OptionsIdentity => _currentOptions;

        public int TotalSetCount { get; private set; }

        public int EnableServerGetCount { get; private set; }

        public int IpConnectionsEnabledGetCount { get; private set; }

        public FakeOptions CurrentOptions =>
            _currentOptions ?? throw new InvalidOperationException("Options are unavailable.");

        public bool PauseWhenOutOfFocus
        {
            get => CurrentOptions.PauseWhenOutOfFocus;
            set
            {
                CurrentOptions.SetFromGuard(value);
                TotalSetCount++;
            }
        }

        public bool EnableServer
        {
            get
            {
                EnableServerGetCount++;
                return CurrentOptions.EnableServer;
            }

            set => CurrentOptions.SetEnableServerFromGuard(value);
        }

        public bool IpConnectionsEnabled
        {
            get
            {
                IpConnectionsEnabledGetCount++;
                return CurrentOptions.IpConnectionsEnabled;
            }

            set => CurrentOptions.SetIpConnectionsEnabledFromGuard(value);
        }

        public FakeOptions DetachOptions()
        {
            FakeOptions current = CurrentOptions;
            _currentOptions = null;
            return current;
        }

        public void AttachOptions(FakeOptions options)
        {
            _currentOptions = options;
        }

        public FakeOptions ReplaceOptions(
            bool pauseWhenOutOfFocus,
            bool enableServer = false,
            bool ipConnectionsEnabled = false)
        {
            _currentOptions = new FakeOptions(
                pauseWhenOutOfFocus,
                enableServer,
                ipConnectionsEnabled);
            return _currentOptions;
        }

        public void ResetCurrentByGame(bool pauseWhenOutOfFocus)
        {
            CurrentOptions.ResetByGame(pauseWhenOutOfFocus);
        }

        public void ResetCurrentNetworkByGame(
            bool enableServer,
            bool ipConnectionsEnabled)
        {
            CurrentOptions.ResetNetworkByGame(
                enableServer,
                ipConnectionsEnabled);
        }
    }

    private sealed class FakeOptions
    {
        public FakeOptions(
            bool pauseWhenOutOfFocus,
            bool enableServer,
            bool ipConnectionsEnabled)
        {
            PauseWhenOutOfFocus = pauseWhenOutOfFocus;
            EnableServer = enableServer;
            IpConnectionsEnabled = ipConnectionsEnabled;
        }

        public bool PauseWhenOutOfFocus { get; private set; }

        public bool EnableServer { get; private set; }

        public bool IpConnectionsEnabled { get; private set; }

        public int SetCount { get; private set; }

        public int EnableServerSetCount { get; private set; }

        public int IpConnectionsEnabledSetCount { get; private set; }

        public bool RejectEnableServerWrites { get; set; }

        public bool RejectIpConnectionsEnabledWrites { get; set; }

        public void SetFromGuard(bool pauseWhenOutOfFocus)
        {
            PauseWhenOutOfFocus = pauseWhenOutOfFocus;
            SetCount++;
        }

        public void SetEnableServerFromGuard(bool enableServer)
        {
            if (!RejectEnableServerWrites)
            {
                EnableServer = enableServer;
            }

            EnableServerSetCount++;
        }

        public void SetIpConnectionsEnabledFromGuard(bool ipConnectionsEnabled)
        {
            if (!RejectIpConnectionsEnabledWrites)
            {
                IpConnectionsEnabled = ipConnectionsEnabled;
            }

            IpConnectionsEnabledSetCount++;
        }

        public void ResetByGame(bool pauseWhenOutOfFocus)
        {
            PauseWhenOutOfFocus = pauseWhenOutOfFocus;
        }

        public void ResetNetworkByGame(
            bool enableServer,
            bool ipConnectionsEnabled)
        {
            EnableServer = enableServer;
            IpConnectionsEnabled = ipConnectionsEnabled;
        }
    }
}

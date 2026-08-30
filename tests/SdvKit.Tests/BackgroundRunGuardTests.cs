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
    public void NormalGameExitRestoresBeforeWritingTheExitingMarker()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "SdvKit.AlwaysOn",
            "ModEntry.cs"))
            .ReplaceLineEndings("\n");

        int handler = source.IndexOf(
            "GameRunner.instance.Exiting += (_, _) => PrepareForExit();",
            StringComparison.Ordinal);
        int preparation = source.IndexOf(
            "private bool PrepareForExit()",
            handler,
            StringComparison.Ordinal);
        int restore = source.IndexOf(
            "_backgroundRun!.RestoreOriginalAndDisable();",
            preparation,
            StringComparison.Ordinal);
        int exitingMarker = source.IndexOf(
            "restore.Succeeded ? \"exiting\" : \"restoreFailed\"",
            restore,
            StringComparison.Ordinal);
        int markerWrite = source.IndexOf(
            "WriteStatus(",
            restore,
            StringComparison.Ordinal);

        Assert.True(handler >= 0);
        Assert.True(preparation > handler);
        Assert.True(restore > preparation);
        Assert.True(exitingMarker > restore);
        Assert.True(markerWrite > restore);
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

        public FakeBackgroundRunState(bool pauseWhenOutOfFocus)
        {
            ReplaceOptions(pauseWhenOutOfFocus);
        }

        public bool IsAvailable => _currentOptions is not null;

        public object? OptionsIdentity => _currentOptions;

        public int TotalSetCount { get; private set; }

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

        public FakeOptions ReplaceOptions(bool pauseWhenOutOfFocus)
        {
            _currentOptions = new FakeOptions(pauseWhenOutOfFocus);
            return _currentOptions;
        }

        public void ResetCurrentByGame(bool pauseWhenOutOfFocus)
        {
            CurrentOptions.ResetByGame(pauseWhenOutOfFocus);
        }
    }

    private sealed class FakeOptions
    {
        public FakeOptions(bool pauseWhenOutOfFocus)
        {
            PauseWhenOutOfFocus = pauseWhenOutOfFocus;
        }

        public bool PauseWhenOutOfFocus { get; private set; }

        public int SetCount { get; private set; }

        public void SetFromGuard(bool pauseWhenOutOfFocus)
        {
            PauseWhenOutOfFocus = pauseWhenOutOfFocus;
            SetCount++;
        }

        public void ResetByGame(bool pauseWhenOutOfFocus)
        {
            PauseWhenOutOfFocus = pauseWhenOutOfFocus;
        }
    }
}

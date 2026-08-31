using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class WindowsForegroundWindowProbeTests
{
    [Theory]
    [InlineData(4242, true, false)]
    [InlineData(9001, false, true)]
    public void NativeWindowAndPidIdentifyWhetherThisProcessOwnsTheForeground(
        int foregroundProcessId,
        bool isCurrentProcess,
        bool isVerifiedUnfocused)
    {
        WindowsForegroundWindowObservation observation =
            WindowsForegroundWindowProbe.FromNativeObservation(
                new IntPtr(12345),
                (uint)foregroundProcessId,
                currentProcessId: 4242);

        Assert.Equal(12345L, observation.WindowHandle);
        Assert.Equal(foregroundProcessId, observation.ProcessId);
        Assert.Equal(isCurrentProcess, observation.IsCurrentProcess);
        Assert.Equal(isVerifiedUnfocused, observation.IsVerifiedUnfocused);
    }

    [Theory]
    [InlineData(0L, 9001U, 4242)]
    [InlineData(12345L, 0U, 4242)]
    [InlineData(12345L, 9001U, 0)]
    [InlineData(12345L, uint.MaxValue, 4242)]
    public void IncompleteNativeForegroundEvidenceIsNeverUnfocusedProof(
        long windowHandle,
        uint foregroundProcessId,
        int currentProcessId)
    {
        WindowsForegroundWindowObservation observation =
            WindowsForegroundWindowProbe.FromNativeObservation(
                new IntPtr(windowHandle),
                foregroundProcessId,
                currentProcessId);

        Assert.Null(observation.WindowHandle);
        Assert.Null(observation.ProcessId);
        Assert.Null(observation.IsCurrentProcess);
        Assert.False(observation.IsVerifiedUnfocused);
    }
}

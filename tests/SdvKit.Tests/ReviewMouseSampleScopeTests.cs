using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewMouseSampleScopeTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(0, "host")]
    [InlineData(0, "farmhand")]
    public void OnlyExactCurrentReviewPlayerCanReadDuringItsUpdate(int screen, string? role)
    {
        object game = new(), input = new(), player = new();
        using var scope = new ReviewMouseSampleScope(game, input, player, screen, "launch", role);
        Assert.True(scope.IsCurrent(game, input, player, screen, "launch", role, true));
        Assert.False(scope.IsCurrent(new object(), input, player, screen, "launch", role, true));
        Assert.False(scope.IsCurrent(game, new object(), player, screen, "launch", role, true));
        Assert.False(scope.IsCurrent(game, input, new object(), screen, "launch", role, true));
        Assert.False(scope.IsCurrent(game, input, player, screen + 1, "launch", role, true));
        Assert.False(scope.IsCurrent(game, input, player, screen, "replacement", role, true));
        Assert.False(scope.IsCurrent(game, input, player, screen, "launch", "peer", true));
        Assert.False(scope.IsCurrent(game, input, player, screen, "launch", role, false));
    }

    [Fact]
    public void WorkerThreadCannotReadEvenWhenEveryGameIdentityMatches()
    {
        object game = new(), input = new(), player = new();
        using var scope = new ReviewMouseSampleScope(game, input, player, 0, "launch", null);
        bool permitted = true;
        var worker = new Thread(() => permitted = scope.IsCurrent(game, input, player, 0, "launch", null, true));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.False(permitted);
        Assert.True(scope.IsCurrent(game, input, player, 0, "launch", null, true));
    }

    [Fact]
    public void FinalizerRevokesScopeEvenWhenUpdateThrowsOrSkipsPublicCompletion()
    {
        object game = new(), input = new(), player = new();
        var scope = new ReviewMouseSampleScope(game, input, player, 0, "launch", null);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            try { throw new InvalidOperationException("Update failed before UpdateTicked."); }
            finally { scope.Dispose(); }
        }));
        Assert.False(scope.IsCurrent(game, input, player, 0, "launch", null, true));
        scope.Dispose();
        Assert.False(scope.IsCurrent(game, input, player, 0, "launch", null, true));
    }

    [Fact]
    public void ReentrantReplacementCannotReviveEarlierPublication()
    {
        object game = new(), input = new(), player = new();
        using var previous = new ReviewMouseSampleScope(game, input, player, 0, "launch", null);
        previous.Dispose();
        using (var replacement = new ReviewMouseSampleScope(game, input, player, 0, "launch", null))
            Assert.True(replacement.IsCurrent(game, input, player, 0, "launch", null, true));
        Assert.False(previous.IsCurrent(game, input, player, 0, "launch", null, true));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void UnownedLaunchCannotPublish(string launch)
    {
        object game = new(), input = new(), player = new();
        using var scope = new ReviewMouseSampleScope(game, input, player, 0, launch, null);
        Assert.False(scope.IsCurrent(game, input, player, 0, launch, null, true));
    }
}

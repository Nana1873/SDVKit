using System.Collections.Concurrent;
using SdvKit.GameSide;

namespace SdvKit.Tests;

public sealed class ReviewResponseMailboxTests
{
    [Fact]
    public void ResponseIsPublishedOnceAndTakenOnce()
    {
        string response = UniquePath();
        ReviewResponseMailbox.Publish(response, "first"u8);

        Assert.Throws<InvalidDataException>(() => ReviewResponseMailbox.Publish(response, "second"u8));
        Assert.True(ReviewResponseMailbox.TryTake(response, out byte[] bytes));
        Assert.Equal("first"u8.ToArray(), bytes);
        Assert.False(ReviewResponseMailbox.TryTake(response, out _));
    }

    [Fact]
    public void ConcurrentPublishersKeepExactlyOneCompleteResponse()
    {
        string response = UniquePath();
        var published = new ConcurrentBag<byte[]>();
        byte[][] candidates = Enumerable.Range(1, 8)
            .Select(value => Enumerable.Repeat((byte)value, 65536).ToArray())
            .ToArray();

        Parallel.ForEach(candidates, bytes =>
        {
            try
            {
                ReviewResponseMailbox.Publish(response, bytes);
                published.Add(bytes);
            }
            catch (InvalidDataException)
            {
                // A competing publisher owns the response key.
            }
        });

        byte[] winner = Assert.Single(published);
        Assert.True(ReviewResponseMailbox.TryTake(response, out byte[] taken));
        Assert.Equal(winner, taken);
    }

    [Fact]
    public void ExpiredResponsesAreDropped()
    {
        string response = UniquePath();
        ReviewResponseMailbox.Publish(
            response,
            "old"u8,
            DateTimeOffset.UtcNow - ReviewResponseMailbox.Lifetime - TimeSpan.FromSeconds(1));

        Assert.False(ReviewResponseMailbox.TryTake(response, out _));
    }

    [Fact]
    public void EmptyResponsesAreRejected() =>
        Assert.Throws<InvalidDataException>(() => ReviewResponseMailbox.Publish(UniquePath(), ReadOnlySpan<byte>.Empty));

    [Fact]
    public void PublishingNeverTouchesTheFileSystem()
    {
        using TemporaryDirectory temporary = new();
        string response = Path.Combine(temporary.Path, "missing", Guid.NewGuid().ToString("N") + ".json");

        ReviewResponseMailbox.Publish(response, "{}"u8);

        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "missing")));
        Assert.True(ReviewResponseMailbox.TryTake(response, out _));
    }

    private static string UniquePath() =>
        Path.Combine(Path.GetTempPath(), "sdvkit-mailbox-tests", Guid.NewGuid().ToString("N") + ".json");
}

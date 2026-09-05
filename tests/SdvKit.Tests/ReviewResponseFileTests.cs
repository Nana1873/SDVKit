using System.Collections.Concurrent;
using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewResponseFileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExistingResponseOrTemporaryEntryIsPreserved(bool temporaryEntry, bool directory)
    {
        using TemporaryDirectory temporary = new();
        string response = Path.Combine(temporary.Path, "response.json");
        string entry = temporaryEntry ? response + ".tmp" : response;
        string sentinel = entry;
        if (directory)
        {
            Directory.CreateDirectory(entry);
            sentinel = Path.Combine(entry, "foreign.txt");
        }
        File.WriteAllText(sentinel, "foreign");

        Assert.Throws<InvalidDataException>(() => ReviewResponseFile.Write(response, "new"u8));

        Assert.Equal("foreign", File.ReadAllText(sentinel));
        if (temporaryEntry)
        {
            Assert.False(File.Exists(response));
        }
    }

    [Fact]
    public void ConcurrentPublishersKeepExactlyOneCompleteResponse()
    {
        using TemporaryDirectory temporary = new();
        string response = Path.Combine(temporary.Path, "response.json");
        var published = new ConcurrentBag<byte[]>();
        byte[][] candidates = Enumerable.Range(1, 8)
            .Select(value => Enumerable.Repeat((byte)value, 65536).ToArray())
            .ToArray();

        Parallel.ForEach(candidates, bytes =>
        {
            try
            {
                ReviewResponseFile.Write(response, bytes);
                published.Add(bytes);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // A competing publisher owns the fixed response name.
            }
        });

        byte[] winner = Assert.Single(published);
        Assert.Equal(winner, File.ReadAllBytes(response));
        Assert.False(File.Exists(response + ".tmp"));
    }

    [Fact]
    public void MissingRuntimeIsRejectedWithoutCreatingDirectories()
    {
        using TemporaryDirectory temporary = new();
        string missing = Path.Combine(temporary.Path, "missing");

        Assert.ThrowsAny<IOException>(() =>
            ReviewResponseFile.Write(Path.Combine(missing, "response.json"), "new"u8));

        Assert.False(Directory.Exists(missing));
    }
}

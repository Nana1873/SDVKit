using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class StopRequestFileTests
{
    [Fact]
    public void WriteAtomicallyPublishesOnlyTheExactLaunchId()
    {
        using TemporaryDirectory temporary = new();
        string runtime = Path.Combine(temporary.Path, ".sdvkit", "lab", "single", "runtime");
        Directory.CreateDirectory(runtime);
        string path = Path.Combine(runtime, "stop.request");
        string first = Guid.NewGuid().ToString("N");
        string second = Guid.NewGuid().ToString("N");

        StopRequestFile.Write(path, first);
        StopRequestFile.Write(path, second);

        Assert.Equal(second, File.ReadAllText(path).Trim());
        Assert.Empty(Directory.EnumerateFiles(runtime, ".stop.request.*.tmp"));
    }
}

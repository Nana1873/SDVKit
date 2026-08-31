using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ModBuildIdentityTests
{
    [Fact]
    public void ComputeIsStableForTheSameDeclaredBuild()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        WriteDeclaredBuild(first, "assembly", "{\"version\":1}");
        WriteDeclaredBuild(second, "assembly", "{\"version\":1}");

        string firstIdentity = ModBuildIdentity.Compute(first.Path);
        string secondIdentity = ModBuildIdentity.Compute(second.Path);

        Assert.Equal(firstIdentity, secondIdentity);
        Assert.True(ModBuildIdentity.IsValid(firstIdentity));
    }

    [Theory]
    [InlineData("SdvKit.AlwaysOn.dll")]
    [InlineData("manifest.json")]
    public void ComputeChangesWhenADeclaredFileChanges(string fileName)
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        WriteDeclaredBuild(first, "assembly", "manifest");
        WriteDeclaredBuild(second, "assembly", "manifest");
        File.AppendAllText(Path.Combine(second.Path, fileName), "changed");

        Assert.NotEqual(
            ModBuildIdentity.Compute(first.Path),
            ModBuildIdentity.Compute(second.Path));
    }

    [Theory]
    [InlineData("SdvKit.AlwaysOn.dll")]
    [InlineData("manifest.json")]
    public void ComputeRejectsAMissingDeclaredFile(string fileName)
    {
        using TemporaryDirectory build = new();
        WriteDeclaredBuild(build, "assembly", "manifest");
        File.Delete(Path.Combine(build.Path, fileName));

        FileNotFoundException exception = Assert.Throws<FileNotFoundException>(
            () => ModBuildIdentity.Compute(build.Path));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:0123")]
    [InlineData("sha256:g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void IsValidRejectsNonCanonicalValues(string? value)
    {
        Assert.False(ModBuildIdentity.IsValid(value));
    }

    private static void WriteDeclaredBuild(
        TemporaryDirectory directory,
        string assembly,
        string manifest)
    {
        directory.WriteFile("SdvKit.AlwaysOn.dll", assembly);
        directory.WriteFile("manifest.json", manifest);
    }
}

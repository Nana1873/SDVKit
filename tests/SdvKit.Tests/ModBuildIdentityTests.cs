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

    [Fact]
    public void ComputeRetainsTheDeclaredAlwaysOnIdentityFormat()
    {
        using TemporaryDirectory build = new();
        WriteDeclaredBuild(build, "assembly", "{\"version\":1}");

        Assert.Equal(
            "sha256:0d36ea2b0af602f5bde4710eb09dddc1ba26bb2dde57c12a9a3e9a5d0d9ce382",
            ModBuildIdentity.Compute(build.Path));
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

    [Fact]
    public void ComputeFileSetIsIndependentOfEnumerationAndCreationOrder()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        first.WriteFile("z-last.txt", "last");
        first.WriteFile("nested/a-first.txt", "first");
        first.WriteFile("middle.txt", "middle");
        second.WriteFile("middle.txt", "middle");
        second.WriteFile("nested/a-first.txt", "first");
        second.WriteFile("z-last.txt", "last");

        Assert.Equal(
            ModBuildIdentity.ComputeFileSet(first.Path),
            ModBuildIdentity.ComputeFileSet(second.Path));
    }

    [Fact]
    public void ComputeFileSetUsesNormalizedNestedPathsAndAllBundledFiles()
    {
        using TemporaryDirectory build = new();
        build.WriteFile("manifest.json", "manifest");
        build.WriteFile("assets/data.json", "asset");
        build.WriteFile("lib/Bundled.dll", "binary");

        string identity = ModBuildIdentity.ComputeFileSet(build.Path);

        Assert.Equal(
            "sha256:b73949eabf346284c9acef1a0d397c159067a3704d19074ab7ed83addf22907e",
            identity);
        Assert.True(ModBuildIdentity.IsValid(identity));
    }

    [Fact]
    public void ComputeFileSetChangesWhenAPathChanges()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        first.WriteFile("assets/first.json", "same");
        second.WriteFile("assets/second.json", "same");

        Assert.NotEqual(
            ModBuildIdentity.ComputeFileSet(first.Path),
            ModBuildIdentity.ComputeFileSet(second.Path));
    }

    [Fact]
    public void ComputeFileSetChangesWhenNestedContentChanges()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        first.WriteFile("lib/Bundled.dll", "first");
        second.WriteFile("lib/Bundled.dll", "second");

        Assert.NotEqual(
            ModBuildIdentity.ComputeFileSet(first.Path),
            ModBuildIdentity.ComputeFileSet(second.Path));
    }

    [Fact]
    public void ComputeFileSetRejectsAnEmptyTree()
    {
        using TemporaryDirectory build = new();
        Directory.CreateDirectory(Path.Combine(build.Path, "empty"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ModBuildIdentity.ComputeFileSet(build.Path));

        Assert.Contains("regular files", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeFileSetRejectsAReparsePointWhenSupported()
    {
        using TemporaryDirectory build = new();
        string target = build.WriteFile("target.txt", "target");
        string link = Path.Combine(build.Path, "linked.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        InvalidDataException result = Assert.Throws<InvalidDataException>(
            () => ModBuildIdentity.ComputeFileSet(build.Path));

        Assert.Contains("reparse point", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchesFileSetAllowsOnlyANewRegularRootConfigJson()
    {
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);
        const string secret = "SharedSecret=runtime-only-value";
        build.WriteFile("config.json", $"not-json::{secret}");

        Assert.False(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: false));
        Assert.True(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));
    }

    [Fact]
    public void MatchesFileSetKeepsPackagedConfigJsonInsideTheNormalIdentity()
    {
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string configPath = build.WriteFile("config.json", "packaged config");
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);

        Assert.True(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: false));
        Assert.True(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));

        File.WriteAllText(configPath, "changed config");

        Assert.False(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));

        File.Delete(configPath);

        Assert.False(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));
    }

    [Theory]
    [InlineData("ExampleMod.dll")]
    [InlineData("manifest.json")]
    [InlineData("LICENSE")]
    public void MatchesFileSetDoesNotHidePackagedFileDriftBehindRootConfigJson(
        string changedFile)
    {
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);
        build.WriteFile("config.json", "runtime config");
        File.AppendAllText(Path.Combine(build.Path, changedFile), "drift");

        Assert.False(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchesFileSetRejectsPackagedFileDeletionOrRenameBehindRootConfigJson(
        bool rename)
    {
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);
        build.WriteFile("config.json", "runtime config");
        string license = Path.Combine(build.Path, "LICENSE");
        if (rename)
        {
            File.Move(license, Path.Combine(build.Path, "COPYING"));
        }
        else
        {
            File.Delete(license);
        }

        Assert.False(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));
    }

    [Theory]
    [InlineData("runtime.tmp")]
    [InlineData("Config.json")]
    [InlineData("nested/config.json")]
    public void MatchesFileSetRejectsEveryOtherRuntimeAddition(string additionalPath)
    {
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);
        if (!string.Equals(additionalPath, "Config.json", StringComparison.Ordinal))
        {
            build.WriteFile("config.json", "runtime config");
        }

        build.WriteFile(additionalPath, "additional runtime file");

        Assert.False(ModBuildIdentity.MatchesFileSet(
            build.Path,
            expectedIdentity,
            allowNewRootConfigJson: true));
    }

    [Fact]
    public void MatchesFileSetRejectsAConfigJsonDirectoryEvenWhenTheFilesStillMatch()
    {
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);
        Directory.CreateDirectory(Path.Combine(build.Path, "config.json"));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ModBuildIdentity.MatchesFileSet(
                build.Path,
                expectedIdentity,
                allowNewRootConfigJson: true));

        Assert.Contains("regular file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchesFileSetRejectsAConfigJsonReparsePointWithoutReadingItsTarget()
    {
        using TemporaryDirectory outside = new();
        using TemporaryDirectory build = new();
        WriteProjectModFileSet(build);
        string expectedIdentity = ModBuildIdentity.ComputeFileSet(build.Path);
        const string secret = "SharedSecret=outside-sentinel";
        string target = outside.WriteFile("external-config.json", secret);
        string link = Path.Combine(build.Path, "config.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        InvalidDataException result = Assert.Throws<InvalidDataException>(() =>
            ModBuildIdentity.MatchesFileSet(
                build.Path,
                expectedIdentity,
                allowNewRootConfigJson: true));

        Assert.Contains("reparse point", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
        Assert.Equal(secret, File.ReadAllText(target));
    }

    [Fact]
    public void ComputeFileReturnsTheCanonicalContentHash()
    {
        using TemporaryDirectory package = new();
        string archive = package.WriteFile("package.zip", "abc");

        Assert.Equal(
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ModBuildIdentity.ComputeFile(archive));
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

    private static void WriteProjectModFileSet(TemporaryDirectory directory)
    {
        directory.WriteFile("ExampleMod.dll", "assembly");
        directory.WriteFile("manifest.json", "manifest");
        directory.WriteFile("LICENSE", "license");
    }
}

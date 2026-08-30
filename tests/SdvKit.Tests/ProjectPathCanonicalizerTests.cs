using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectPathCanonicalizerTests
{
    [Fact]
    public void ExistingAbsoluteDirectoryReturnsAnAbsolutePathWithoutATrailingSeparator()
    {
        using TemporaryDirectory project = new();

        string canonical = ProjectPathCanonicalizer.CanonicalizeExistingDirectory(
            project.Path + Path.DirectorySeparatorChar);

        Assert.True(Path.IsPathFullyQualified(canonical));
        Assert.Equal(
            canonical,
            Path.TrimEndingDirectorySeparator(canonical),
            PathComparison);
        Assert.True(Directory.Exists(canonical));
    }

    [Fact]
    public void WindowsDirectoryAliasResolvesToTheSameFinalDosPathWhenLinksAreSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        using TemporaryDirectory aliases = new();
        string alias = Path.Combine(aliases.Path, "project-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, project.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
            return;
        }

        string canonicalProject =
            ProjectPathCanonicalizer.CanonicalizeExistingDirectory(project.Path);
        string canonicalAlias =
            ProjectPathCanonicalizer.CanonicalizeExistingDirectory(alias);

        Assert.Equal(canonicalProject, canonicalAlias, StringComparer.OrdinalIgnoreCase);
        Assert.False(canonicalAlias.StartsWith(@"\\?\", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAbsoluteDirectoryFailsAsAControlledInvalidOperation()
    {
        using TemporaryDirectory project = new();
        string missing = Path.Combine(project.Path, "missing");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProjectPathCanonicalizer.CanonicalizeExistingDirectory(missing));

        Assert.Contains("project root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingFileFailsAsAControlledInvalidOperation()
    {
        using TemporaryDirectory project = new();
        string file = project.WriteFile("not-a-directory.txt", "content");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProjectPathCanonicalizer.CanonicalizeExistingDirectory(file));

        Assert.Contains("directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeDirectoryFailsAsAControlledInvalidOperation()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ProjectPathCanonicalizer.CanonicalizeExistingDirectory("relative-project"));

        Assert.Contains("fully qualified", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer PathComparison => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

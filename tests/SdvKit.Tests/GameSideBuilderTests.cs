using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class GameSideBuilderTests
{
    [Fact]
    public void BuilderKeepsBuildDataLocalAndInstallsOnlyDllAndManifest()
    {
        using TemporaryDirectory repository = new();
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        repository.WriteFile("SDVKit.sln");
        repository.WriteFile("src/SdvKit.GameSide/SdvKit.GameSide.csproj");
        repository.WriteFile("src/SdvKit.GameSide/manifest.json", "{\"UniqueID\":\"SDVKit.GameSide\"}");
        var runner = new SuccessfulBuildRunner();
        var builder = new GameSideBuilder(runner, () => repository.Path);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        GameSideBuildResult result = builder.BuildAndInstall(game.Path, paths);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(paths.BuildPath, "game-side-build.log"), result.LogPath);
        Assert.True(File.Exists(result.LogPath));
        Assert.Equal(
            ["SdvKit.GameSide.dll", "manifest.json"],
            Directory.GetFiles(paths.GameSideModPath)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());

        DotNetBuildCommand command = Assert.Single(runner.Commands);
        Assert.Equal(repository.Path, command.WorkingDirectory);
        Assert.Equal("build", command.Arguments[0]);
        Assert.Contains(
            $"--property:SdvGamePath={game.Path}",
            command.Arguments);
        Assert.Contains(
            command.Arguments,
            argument => argument.StartsWith(
                $"--property:BaseIntermediateOutputPath={paths.BuildPath}",
                StringComparison.Ordinal));
        Assert.Contains(
            command.Arguments,
            argument => argument.StartsWith(
                $"--property:MSBuildProjectExtensionsPath={paths.BuildPath}",
                StringComparison.Ordinal));
        string outputPath = command.Arguments[command.Arguments.IndexOf("--output") + 1];
        Assert.True(IsBelow(outputPath, paths.BuildPath));
    }

    [Fact]
    public void FailedBuildIsReportedAndDoesNotInstallTheMod()
    {
        using TemporaryDirectory repository = new();
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        repository.WriteFile("SDVKit.sln");
        repository.WriteFile("src/SdvKit.GameSide/SdvKit.GameSide.csproj");
        repository.WriteFile("src/SdvKit.GameSide/manifest.json", "{}");
        var builder = new GameSideBuilder(
            new FailingBuildRunner(),
            () => repository.Path);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        GameSideBuildResult result = builder.BuildAndInstall(game.Path, paths);

        Assert.False(result.Succeeded);
        Assert.Contains("exit code 7", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.GameSideModPath));
        Assert.Contains("compiler error", File.ReadAllText(result.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceRootLocatorWalksUpToTheGameSideProject()
    {
        using TemporaryDirectory repository = new();
        repository.WriteFile("src/SdvKit.GameSide/SdvKit.GameSide.csproj");
        string nested = Path.Combine(repository.Path, "src", "SdvKit.Cli", "bin", "Release");
        Directory.CreateDirectory(nested);

        string result = GameSideSourceRootLocator.Find(nested);

        Assert.Equal(repository.Path, result);
    }

    [Fact]
    public void SourceRootLocatorPrefersTheNearestPackagedGameSideProject()
    {
        using TemporaryDirectory repository = new();
        repository.WriteFile("src/SdvKit.GameSide/SdvKit.GameSide.csproj");
        string packageRoot = Path.Combine(
            repository.Path,
            ".sdvkit",
            "distribution",
            "extracted",
            "SDVKit-0.1.0-win-x64");
        string packagedProject = Path.Combine(
            packageRoot,
            "src",
            "SdvKit.GameSide",
            "SdvKit.GameSide.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(packagedProject)!);
        File.WriteAllText(packagedProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string result = GameSideSourceRootLocator.Find(packageRoot);

        Assert.Equal(packageRoot, result);
    }

    private static bool IsBelow(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return !relative.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }

    private sealed class SuccessfulBuildRunner : IDotNetBuildRunner
    {
        public List<DotNetBuildCommand> Commands { get; } = [];

        public DotNetBuildOutput Run(DotNetBuildCommand command)
        {
            Commands.Add(command);
            int outputOption = command.Arguments.IndexOf("--output");
            string outputPath = command.Arguments[outputOption + 1];
            Directory.CreateDirectory(outputPath);
            File.WriteAllText(Path.Combine(outputPath, "SdvKit.GameSide.dll"), "built");
            File.WriteAllText(Path.Combine(outputPath, "extra.pdb"), "build-only");
            return new DotNetBuildOutput(0, "build succeeded", string.Empty);
        }
    }

    private sealed class FailingBuildRunner : IDotNetBuildRunner
    {
        public DotNetBuildOutput Run(DotNetBuildCommand command)
        {
            return new DotNetBuildOutput(7, string.Empty, "compiler error");
        }
    }
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}

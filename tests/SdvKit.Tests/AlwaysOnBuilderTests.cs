using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class AlwaysOnBuilderTests
{
    [Fact]
    public void BuilderKeepsBuildDataLocalAndInstallsOnlyDllAndManifest()
    {
        using TemporaryDirectory repository = new();
        using TemporaryDirectory project = new();
        using TemporaryDirectory game = new();
        repository.WriteFile("SDVKit.sln");
        repository.WriteFile("src/SdvKit.AlwaysOn/SdvKit.AlwaysOn.csproj");
        repository.WriteFile("src/SdvKit.AlwaysOn/manifest.json", "{\"UniqueID\":\"SDVKit.AlwaysOn\"}");
        var runner = new SuccessfulBuildRunner();
        var builder = new AlwaysOnBuilder(runner, () => repository.Path);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        AlwaysOnBuildResult result = builder.BuildAndInstall(game.Path, paths);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(paths.BuildPath, "always-on-build.log"), result.LogPath);
        Assert.True(File.Exists(result.LogPath));
        Assert.Equal(
            ["SdvKit.AlwaysOn.dll", "manifest.json"],
            Directory.GetFiles(paths.AlwaysOnModPath)
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
        repository.WriteFile("src/SdvKit.AlwaysOn/SdvKit.AlwaysOn.csproj");
        repository.WriteFile("src/SdvKit.AlwaysOn/manifest.json", "{}");
        var builder = new AlwaysOnBuilder(
            new FailingBuildRunner(),
            () => repository.Path);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        AlwaysOnBuildResult result = builder.BuildAndInstall(game.Path, paths);

        Assert.False(result.Succeeded);
        Assert.Contains("exit code 7", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.AlwaysOnModPath));
        Assert.Contains("compiler error", File.ReadAllText(result.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryRootLocatorWalksUpToTheSolution()
    {
        using TemporaryDirectory repository = new();
        repository.WriteFile("SDVKit.sln");
        string nested = Path.Combine(repository.Path, "src", "SdvKit.Cli", "bin", "Release");
        Directory.CreateDirectory(nested);

        string result = RepositoryRootLocator.Find(nested);

        Assert.Equal(repository.Path, result);
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
            File.WriteAllText(Path.Combine(outputPath, "SdvKit.AlwaysOn.dll"), "built");
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

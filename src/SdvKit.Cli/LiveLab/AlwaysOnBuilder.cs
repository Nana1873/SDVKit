using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SdvKit.Cli.LiveLab;

internal sealed record AlwaysOnBuildResult(
    bool Succeeded,
    string LogPath,
    string? Error);

internal interface IAlwaysOnBuilder
{
    AlwaysOnBuildResult BuildAndInstall(string gamePath, LiveLabPaths paths);
}

internal sealed class AlwaysOnBuilder : IAlwaysOnBuilder
{
    internal const string ProjectRelativePath = "src/SdvKit.AlwaysOn/SdvKit.AlwaysOn.csproj";
    private const string AssemblyFileName = "SdvKit.AlwaysOn.dll";
    private const string ManifestFileName = "manifest.json";

    private readonly IDotNetBuildRunner _runner;
    private readonly Func<string> _findSourceRoot;

    public AlwaysOnBuilder()
        : this(
            new DotNetBuildRunner(),
            () => AlwaysOnSourceRootLocator.Find(AppContext.BaseDirectory))
    {
    }

    internal AlwaysOnBuilder(
        IDotNetBuildRunner runner,
        Func<string> findSourceRoot)
    {
        _runner = runner;
        _findSourceRoot = findSourceRoot;
    }

    public AlwaysOnBuildResult BuildAndInstall(string gamePath, LiveLabPaths paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        ArgumentNullException.ThrowIfNull(paths);

        paths.EnsureDirectories();
        string absoluteGamePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        string sourceRoot = _findSourceRoot();
        string projectPath = Path.GetFullPath(ProjectRelativePath, sourceRoot);
        string sourceManifestPath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            ManifestFileName);
        string outputPath = Path.Combine(paths.BuildPath, "output");
        string intermediatePath = Path.Combine(paths.BuildPath, "obj") + Path.DirectorySeparatorChar;
        string logPath = Path.Combine(paths.BuildPath, "always-on-build.log");

        if (!File.Exists(projectPath))
        {
            return Failure(logPath, $"AlwaysOn project was not found: {projectPath}");
        }

        if (!File.Exists(sourceManifestPath))
        {
            return Failure(logPath, $"AlwaysOn manifest was not found: {sourceManifestPath}");
        }

        LiveLabPaths.RejectReparsePointsBelow(paths.SingleRoot);
        RecreateDirectory(outputPath);
        RecreateDirectory(intermediatePath);

        var command = new DotNetBuildCommand(
            sourceRoot,
            [
                "build",
                projectPath,
                "--configuration",
                "Release",
                "--output",
                outputPath,
                $"--property:SdvGamePath={absoluteGamePath}",
                $"--property:BaseIntermediateOutputPath={intermediatePath}",
                $"--property:MSBuildProjectExtensionsPath={intermediatePath}",
            ]);
        DotNetBuildOutput output;
        try
        {
            output = _runner.Run(command);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or System.ComponentModel.Win32Exception)
        {
            WriteLog(logPath, -1, string.Empty, exception.Message);
            return Failure(logPath, $"AlwaysOn build could not start: {exception.Message}");
        }

        WriteLog(logPath, output.ExitCode, output.StandardOutput, output.StandardError);
        if (output.ExitCode != 0)
        {
            return Failure(logPath, $"AlwaysOn build failed with exit code {output.ExitCode}.");
        }

        string sourceAssemblyPath = Path.Combine(outputPath, AssemblyFileName);
        if (!File.Exists(sourceAssemblyPath))
        {
            return Failure(logPath, $"AlwaysOn build did not produce {AssemblyFileName}.");
        }

        LiveLabPaths.RejectReparsePointsBelow(paths.SingleRoot);
        RecreateDirectory(paths.AlwaysOnModPath);
        string installedAssemblyPath = Path.Combine(paths.AlwaysOnModPath, AssemblyFileName);
        string installedManifestPath = Path.Combine(paths.AlwaysOnModPath, ManifestFileName);
        File.Copy(sourceAssemblyPath, installedAssemblyPath, overwrite: false);
        File.Copy(sourceManifestPath, installedManifestPath, overwrite: false);

        return new AlwaysOnBuildResult(true, logPath, null);
    }

    private static AlwaysOnBuildResult Failure(string logPath, string error)
    {
        return new AlwaysOnBuildResult(false, logPath, error);
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void WriteLog(
        string logPath,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        var content = new StringBuilder();
        content.Append("exitCode: ");
        content.AppendLine(exitCode.ToString(CultureInfo.InvariantCulture));
        content.AppendLine("stdout:");
        content.AppendLine(standardOutput);
        content.AppendLine("stderr:");
        content.AppendLine(standardError);
        File.WriteAllText(logPath, content.ToString());
    }
}

internal sealed record DotNetBuildCommand(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

internal sealed record DotNetBuildOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal interface IDotNetBuildRunner
{
    DotNetBuildOutput Run(DotNetBuildCommand command);
}

internal sealed class DotNetBuildRunner : IDotNetBuildRunner
{
    public DotNetBuildOutput Run(DotNetBuildCommand command)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("dotnet build did not start.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        return new DotNetBuildOutput(
            process.ExitCode,
            standardOutput.Result,
            standardError.Result);
    }
}

internal static class AlwaysOnSourceRootLocator
{
    public static string Find(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    AlwaysOnBuilder.ProjectRelativePath)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find {AlwaysOnBuilder.ProjectRelativePath} above "
            + $"{Path.GetFullPath(startPath)}.");
    }
}

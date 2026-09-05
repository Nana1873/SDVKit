using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace SdvKit.Cli;

internal sealed record ProjectBuildReport(
    int SchemaVersion,
    string Root,
    string Kind,
    string? ProjectFile,
    string Configuration,
    string? Log,
    IReadOnlyList<ProjectProblem> Problems);

internal sealed record DotNetBuildCommand(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

internal sealed record DotNetBuildResult(
    int? ExitCode,
    string Output,
    string? StartError,
    bool LogWritten = true);

internal delegate DotNetBuildResult DotNetBuildRunner(DotNetBuildCommand command);

internal sealed record ModBuildTarget(
    ProjectInspectionReport Inspection,
    string ProjectFile,
    ProjectManifestSummary Manifest);

internal sealed record ModBuildTargetResolution(
    ProjectInspectionReport Inspection,
    ModBuildTarget? Target,
    IReadOnlyList<ProjectProblem> Problems);

internal static class ProjectBuilder
{
    private static readonly HashSet<string> ExcludedSelectionDirectories = new(["..", ".git", ".sdvkit", "bin", "obj"], StringComparer.OrdinalIgnoreCase);

    public const string Configuration = "Release";
    public const string BuildLogPath = ".sdvkit/logs/build.log";
    public const string PackageLogPath = ".sdvkit/logs/package.log";
    public const string BuildPropsPath = ".sdvkit/build/sdvkit.build.props";
    public const string BuildTargetsPath = ".sdvkit/build/sdvkit.build.targets";

    public static ProjectBuildReport Build(
        string path,
        Func<DoctorReport> discoverInstallations,
        DotNetBuildRunner? runner = null,
        string? projectFile = null)
    {
        return Build(
            path,
            discoverInstallations,
            stateDirectory: null,
            runner,
            projectFile);
    }

    internal static ProjectBuildReport BuildForReview(
        string path,
        string stateDirectory,
        Func<DoctorReport> discoverInstallations,
        DotNetBuildRunner? runner = null,
        string? projectFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return Build(
            path,
            discoverInstallations,
            Path.GetFullPath(stateDirectory),
            runner,
            projectFile);
    }

    private static ProjectBuildReport Build(
        string path,
        Func<DoctorReport> discoverInstallations,
        string? stateDirectory,
        DotNetBuildRunner? runner,
        string? projectFile)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        ModBuildTargetResolution resolution = ResolveTarget(path, projectFile);
        if (resolution.Target is null)
        {
            return Report(resolution, null, resolution.Problems);
        }

        ProjectProblem? gameProblem = GetGamePath(discoverInstallations(), out string? gamePath);
        if (gameProblem is not null)
        {
            return Report(resolution, null, [gameProblem]);
        }

        string root = resolution.Target.Inspection.Root;
        if (stateDirectory is null)
        {
            ProjectProblem? stateProblem = CheckStateDirectory(root);
            if (stateProblem is not null)
            {
                return Report(resolution, null, [stateProblem]);
            }
        }

        string stateRoot = stateDirectory ?? Path.Combine(root, ".sdvkit");
        string logPath = Path.Combine(stateRoot, "logs", "build.log");
        string reportLogPath = stateDirectory is null ? BuildLogPath : logPath;
        string artifactsPath = Path.Combine(stateRoot, "build");
        ProjectProblem? outputProblem = PrepareOutputIsolation(
            artifactsPath,
            "buildOutputUnavailable",
            stateDirectory);
        if (outputProblem is not null)
        {
            return Report(resolution, null, [outputProblem]);
        }

        DotNetBuildCommand command = CreateCommand(
            resolution.Target,
            artifactsPath,
            gamePath!,
            enableZip: false,
            zipPath: null);
        DotNetBuildResult result = RunAndLog(
            command,
            logPath,
            runner ?? RunDotNet,
            stateDirectory);
        IReadOnlyList<ProjectProblem> problems = ProcessProblems(
            result,
            reportLogPath,
            "buildFailed",
            "buildLogUnavailable");
        return Report(resolution, reportLogPath, problems);
    }

    internal static ModBuildTargetResolution ResolveTarget(string path, string? selectedProject = null)
    {
        ProjectInspectionReport inspection = InspectTarget(path, selectedProject);
        if (inspection.Problems.Count > 0)
        {
            return new ModBuildTargetResolution(inspection, null, inspection.Problems);
        }

        if (!string.Equals(inspection.Kind, ProjectInspectionReport.SmapiMod, StringComparison.Ordinal)
            && !string.Equals(inspection.Kind, ProjectInspectionReport.Hybrid, StringComparison.Ordinal))
        {
            return Failure(inspection, "projectNotBuildable");
        }

        if (inspection.ProjectFiles.Count == 0)
        {
            return Failure(inspection, "projectFileNotFound");
        }

        if (selectedProject is null && inspection.ProjectFiles.Count > 1)
        {
            return Failure(inspection, "projectFileAmbiguous");
        }

        string selectedFile = selectedProject is null ? inspection.ProjectFiles[0] : inspection.ProjectFiles.Single();
        ProjectManifestSummary[] modManifests = inspection.Manifests
            .Where(manifest => (selectedProject is null || RelativeDirectory(manifest.Path) == RelativeDirectory(selectedFile)) && string.Equals(
                manifest.Kind,
                ProjectInspectionReport.SmapiMod,
                StringComparison.Ordinal))
            .ToArray();
        if (modManifests.Length != 1)
        {
            return Failure(inspection, "modManifestAmbiguous");
        }

        if (!string.Equals(
            RelativeDirectory(selectedFile),
            RelativeDirectory(modManifests[0].Path),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            return Failure(inspection, "projectManifestMismatch");
        }

        string projectFile = Path.Combine(
            inspection.Root,
            FromSlashPath(selectedFile));
        try
        {
            if ((File.GetAttributes(inspection.Root) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(projectFile) & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(inspection, "reparsePointNotAllowed");
            }
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return Failure(inspection, "pathUnreadable");
        }

        return new ModBuildTargetResolution(
            inspection,
            new ModBuildTarget(inspection, projectFile, modManifests[0]),
            []);
    }

    internal static ProjectInspectionReport InspectTarget(string path, string? selectedProject)
    {
        if (selectedProject is null) return ProjectInspector.Inspect(path);
        ProjectInspectionReport Invalid(string code) => new(1, path, ProjectInspectionReport.Unknown, [], [], [new(code, selectedProject)]);
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string project = Path.GetFullPath(selectedProject, root);
            string relative = Path.GetRelativePath(root, project);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(selectedProject) || parts.Any(ExcludedSelectionDirectories.Contains)
                || !string.Equals(Path.GetExtension(project), ".csproj", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(project)) return Invalid("projectSelectionInvalid");
            if (!ReviewStatePathIsPlain(root, project, allowFinalFile: true)) return Invalid("reparsePointNotAllowed");
            string selectedRoot = Path.GetDirectoryName(project)!;
            if (!ReviewStatePathIsPlain(root, Path.Combine(selectedRoot, "manifest.json"), allowFinalFile: true)) return Invalid("reparsePointNotAllowed");
            ProjectInspectionReport inspection = ProjectInspector.Inspect(selectedRoot);
            string Rebase(string value) => RelativePath(root, Path.Combine(selectedRoot, FromSlashPath(value)));
            if ((inspection.Problems.Count == 0 || inspection.Problems.All(problem => problem.Code == "manifestNotFound")) && !inspection.Manifests.Any(manifest => RelativeDirectory(manifest.Path).Length == 0 && manifest.Kind == ProjectInspectionReport.SmapiMod))
                return Invalid("projectManifestMismatch");
            return inspection with
            {
                Root = root,
                ProjectFiles = [RelativePath(root, project)],
                Manifests = inspection.Manifests.Select(manifest => manifest with { Path = Rebase(manifest.Path) }).ToArray(),
                Problems = inspection.Problems.Select(problem => problem with { Path = problem.Path is null ? null : Rebase(problem.Path) }).ToArray(),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException or UnauthorizedAccessException)
        {
            return Invalid("projectSelectionInvalid");
        }
    }

    internal static ProjectProblem? GetGamePath(DoctorReport doctor, out string? gamePath)
    {
        ArgumentNullException.ThrowIfNull(doctor);

        if (string.Equals(doctor.Status, DoctorReport.Ready, StringComparison.Ordinal)
            && doctor.Installations.Count == 1)
        {
            gamePath = doctor.Installations[0].GamePath;
            return null;
        }

        gamePath = null;
        return new ProjectProblem(
            string.Equals(doctor.Status, DoctorReport.Ambiguous, StringComparison.Ordinal)
                ? "gameInstallationAmbiguous"
                : "gameInstallationNotFound",
            null);
    }

    internal static DotNetBuildCommand CreateCommand(
        ModBuildTarget target,
        string artifactsPath,
        string gamePath,
        bool enableZip,
        string? zipPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(artifactsPath);
        ArgumentNullException.ThrowIfNull(gamePath);

        var arguments = new List<string>
        {
            "build",
            target.ProjectFile,
            "--configuration",
            Configuration,
            "--nologo",
            $"-p:DirectoryBuildPropsPath={EscapeMsBuildPropertyValue(Path.Combine(artifactsPath, Path.GetFileName(BuildPropsPath)))}",
            "-p:ImportDirectoryBuildProps=true",
            $"-p:DirectoryBuildTargetsPath={EscapeMsBuildPropertyValue(Path.Combine(artifactsPath, Path.GetFileName(BuildTargetsPath)))}",
            "-p:ImportDirectoryBuildTargets=true",
            "-p:UseArtifactsOutput=false",
            $"-p:EnableModZip={enableZip.ToString().ToLowerInvariant()}",
            $"-p:GamePath={EscapeMsBuildPropertyValue(gamePath)}",
        };
        if (enableZip)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
            arguments.Add($"-p:ModZipPath={EscapeMsBuildPropertyValue(zipPath)}");
        }

        arguments.Add("-p:EnableModDeploy=false");

        return new DotNetBuildCommand(target.Inspection.Root, arguments);
    }

    internal static ProjectProblem? PrepareOutputIsolation(
        string artifactsPath,
        string failureCode,
        string? reviewStateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(artifactsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);

        string propsPath = Path.Combine(artifactsPath, Path.GetFileName(BuildPropsPath));
        string targetsPath = Path.Combine(artifactsPath, Path.GetFileName(BuildTargetsPath));
        string baseOutputPath = CreateProjectScopedPathValue(
            artifactsPath,
            "bin",
            "_SdvKitProjectKey");
        string baseIntermediateOutputPath = CreateProjectScopedPathValue(
            artifactsPath,
            "obj",
            "_SdvKitProjectKey");
        string propsContents = $"""
            <Project>
              <PropertyGroup>
                <_SdvKitOriginalPropsDir>$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildProjectDirectory), 'Directory.Build.props'))</_SdvKitOriginalPropsDir>
                <_SdvKitOriginalProps Condition="'$(_SdvKitOriginalPropsDir)' != ''">$(_SdvKitOriginalPropsDir){Path.DirectorySeparatorChar}Directory.Build.props</_SdvKitOriginalProps>
              </PropertyGroup>
              <Import Project="$(_SdvKitOriginalProps)" Condition="'$(_SdvKitOriginalProps)' != '' and Exists('$(_SdvKitOriginalProps)')" />
              <PropertyGroup>
                <_SdvKitProjectKey>$(MSBuildProjectName)-$([MSBuild]::StableStringHash(&quot;$(MSBuildProjectFullPath)&quot;))</_SdvKitProjectKey>
                <DefaultItemExcludes>$(DefaultItemExcludes);$(MSBuildProjectDirectory)/obj/**</DefaultItemExcludes>
                <BaseOutputPath>{SecurityElement.Escape(baseOutputPath)}</BaseOutputPath>
                <BaseIntermediateOutputPath>{SecurityElement.Escape(baseIntermediateOutputPath)}</BaseIntermediateOutputPath>
                <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
              </PropertyGroup>
            </Project>
            """;
        string guardOutputPath = CreateProjectScopedPathValue(
            artifactsPath,
            "bin",
            "_SdvKitGuardProjectKey");
        string guardIntermediateOutputPath = CreateProjectScopedPathValue(
            artifactsPath,
            "obj",
            "_SdvKitGuardProjectKey");
        string targetsContents = $"""
            <Project InitialTargets="_SdvKitValidateOutputIsolation">
              <PropertyGroup>
                <_SdvKitOriginalTargetsDir>$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildProjectDirectory), 'Directory.Build.targets'))</_SdvKitOriginalTargetsDir>
                <_SdvKitOriginalTargets Condition="'$(_SdvKitOriginalTargetsDir)' != ''">$(_SdvKitOriginalTargetsDir){Path.DirectorySeparatorChar}Directory.Build.targets</_SdvKitOriginalTargets>
              </PropertyGroup>
              <Import Project="$(_SdvKitOriginalTargets)" Condition="'$(_SdvKitOriginalTargets)' != '' and Exists('$(_SdvKitOriginalTargets)')" />
              <Target Name="_SdvKitValidateOutputIsolation">
                <PropertyGroup>
                  <_SdvKitGuardProjectKey>$(MSBuildProjectName)-$([MSBuild]::StableStringHash(&quot;$(MSBuildProjectFullPath)&quot;))</_SdvKitGuardProjectKey>
                  <_SdvKitGuardOutputRoot>{SecurityElement.Escape(guardOutputPath)}</_SdvKitGuardOutputRoot>
                  <_SdvKitGuardIntermediateRoot>{SecurityElement.Escape(guardIntermediateOutputPath)}</_SdvKitGuardIntermediateRoot>
                </PropertyGroup>
                <ItemGroup>
                  <_SdvKitOutputPath Include="$(BaseOutputPath)" Condition="'$(BaseOutputPath)' != ''" />
                  <_SdvKitOutputPath Include="$(OutputPath)" Condition="'$(OutputPath)' != ''" />
                  <_SdvKitOutputPath Include="$(OutDir)" Condition="'$(OutDir)' != ''" />
                  <_SdvKitOutputPath Include="$(TargetDir)" Condition="'$(TargetDir)' != ''" />
                  <_SdvKitIntermediatePath Include="$(BaseIntermediateOutputPath)" Condition="'$(BaseIntermediateOutputPath)' != ''" />
                  <_SdvKitIntermediatePath Include="$(IntermediateOutputPath)" Condition="'$(IntermediateOutputPath)' != ''" />
                  <_SdvKitIntermediatePath Include="$(MSBuildProjectExtensionsPath)" Condition="'$(MSBuildProjectExtensionsPath)' != ''" />
                </ItemGroup>
                <FindUnderPath Path="$(_SdvKitGuardOutputRoot)" Files="@(_SdvKitOutputPath->'%(FullPath)')" UpdateToAbsolutePaths="true">
                  <Output TaskParameter="OutOfPath" ItemName="_SdvKitOutputOutside" />
                </FindUnderPath>
                <FindUnderPath Path="$(_SdvKitGuardIntermediateRoot)" Files="@(_SdvKitIntermediatePath->'%(FullPath)')" UpdateToAbsolutePaths="true">
                  <Output TaskParameter="OutOfPath" ItemName="_SdvKitIntermediateOutside" />
                </FindUnderPath>
                <Error Condition="'@(_SdvKitOutputOutside)' != ''" Text="SDVKit output isolation rejected an output path outside .sdvkit/build." />
                <Error Condition="'@(_SdvKitIntermediateOutside)' != ''" Text="SDVKit output isolation rejected an intermediate path outside .sdvkit/build." />
              </Target>
            </Project>
            """;

        try
        {
            if (reviewStateDirectory is not null
                && (!ReviewStateTreeIsPlain(reviewStateDirectory)
                    || !ReviewStatePathIsPlain(
                        reviewStateDirectory,
                        artifactsPath,
                        allowFinalFile: false)))
            {
                return new ProjectProblem(failureCode, artifactsPath);
            }

            Directory.CreateDirectory(artifactsPath);
            if (reviewStateDirectory is not null
                && !ReviewStatePathIsPlain(
                    reviewStateDirectory,
                    artifactsPath,
                    allowFinalFile: false))
            {
                return new ProjectProblem(failureCode, artifactsPath);
            }

            if (reviewStateDirectory is not null
                && !ReviewStatePathIsPlain(
                    reviewStateDirectory,
                    propsPath,
                    allowFinalFile: true))
            {
                return new ProjectProblem(failureCode, propsPath);
            }

            WriteBuildFile(propsPath, propsContents);
            if (reviewStateDirectory is not null
                && !ReviewStatePathIsPlain(
                    reviewStateDirectory,
                    targetsPath,
                    allowFinalFile: true))
            {
                return new ProjectProblem(failureCode, targetsPath);
            }

            WriteBuildFile(targetsPath, targetsContents);
            return null;
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return new ProjectProblem(failureCode, ".sdvkit/build");
        }
    }

    private static string CreateProjectScopedPathValue(
        string artifactsPath,
        string directoryName,
        string projectKeyProperty)
    {
        string prefix = EscapeMsBuildPropertyValue(WithTrailingSeparator(
            Path.Combine(artifactsPath, directoryName)));
        return $"{prefix}$({projectKeyProperty}){Path.DirectorySeparatorChar}";
    }

    private static void WriteBuildFile(string path, string contents)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    internal static ProjectProblem? CheckStateDirectory(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        string statePath = Path.Combine(root, ".sdvkit");
        try
        {
            if (File.Exists(statePath) && !Directory.Exists(statePath))
            {
                return new ProjectProblem("unsafeStateDirectory", ".sdvkit");
            }

            if (!Directory.Exists(statePath))
            {
                return null;
            }

            var pending = new Stack<string>();
            pending.Push(statePath);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return new ProjectProblem("unsafeStateDirectory", RelativePath(root, current));
                }

                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return new ProjectProblem("unsafeStateDirectory", RelativePath(root, entry));
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return new ProjectProblem("unsafeStateDirectory", ".sdvkit");
        }
    }

    internal static DotNetBuildResult RunAndLog(
        DotNetBuildCommand command,
        string logPath,
        DotNetBuildRunner runner,
        string? reviewStateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(logPath);
        ArgumentNullException.ThrowIfNull(runner);

        if (reviewStateDirectory is not null
            && !ReviewStateTreeIsPlain(reviewStateDirectory))
        {
            return new DotNetBuildResult(
                null,
                string.Empty,
                "The isolated review state contains a reparse point.",
                LogWritten: false);
        }

        DotNetBuildResult result;
        try
        {
            result = runner(command);
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            result = new DotNetBuildResult(null, string.Empty, exception.Message);
        }

        try
        {
            string? logDirectory = Path.GetDirectoryName(logPath);
            if (reviewStateDirectory is not null
                && (!ReviewStateTreeIsPlain(reviewStateDirectory)
                    || logDirectory is null
                    || !ReviewStatePathIsPlain(
                        reviewStateDirectory,
                        logDirectory,
                        allowFinalFile: false)
                    || !ReviewStatePathIsPlain(
                        reviewStateDirectory,
                        logPath,
                        allowFinalFile: true)
                    || PathExists(logPath)))
            {
                return new DotNetBuildResult(
                    result.ExitCode,
                    result.Output,
                    result.StartError,
                    LogWritten: false);
            }

            if (logDirectory is not null)
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (reviewStateDirectory is not null
                && (!ReviewStatePathIsPlain(
                        reviewStateDirectory,
                        logDirectory!,
                        allowFinalFile: false)
                    || !ReviewStatePathIsPlain(
                        reviewStateDirectory,
                        logPath,
                        allowFinalFile: true)))
            {
                return new DotNetBuildResult(
                    result.ExitCode,
                    result.Output,
                    result.StartError,
                    LogWritten: false);
            }

            using var stream = new FileStream(
                logPath,
                reviewStateDirectory is null ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(FormatLog(command, result));
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return new DotNetBuildResult(
                result.ExitCode,
                result.Output,
                result.StartError,
                LogWritten: false);
        }

        return result;
    }

    internal static bool ReviewStatePathIsPlain(
        string stateDirectory,
        string path,
        bool allowFinalFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string stateRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(stateDirectory));
            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(stateRoot, candidate, comparison)
                && !candidate.StartsWith(
                    stateRoot + Path.DirectorySeparatorChar,
                    comparison))
            {
                return false;
            }

            return ExistingPathComponentsArePlain(candidate, allowFinalFile);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool ReviewStateTreeIsPlain(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        try
        {
            string stateRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(stateDirectory));
            if (!ReviewStatePathIsPlain(
                    stateRoot,
                    stateRoot,
                    allowFinalFile: false))
            {
                return false;
            }

            if (!Directory.Exists(stateRoot))
            {
                return true;
            }

            var pending = new Stack<string>();
            pending.Push(stateRoot);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ExistingPathComponentsArePlain(
        string path,
        bool allowFinalFile)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (!TryGetAttributes(root, out FileAttributes rootAttributes)
            || (rootAttributes & FileAttributes.ReparsePoint) != 0
            || (rootAttributes & FileAttributes.Directory) == 0)
        {
            return false;
        }

        string[] segments = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!TryGetAttributes(current, out FileAttributes attributes))
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            bool final = index == segments.Length - 1;
            if ((attributes & FileAttributes.Directory) == 0
                && (!final || !allowFinalFile))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathExists(string path) =>
        TryGetAttributes(path, out _);

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    internal static IReadOnlyList<ProjectProblem> ProcessProblems(
        DotNetBuildResult result,
        string logPath,
        string failureCode,
        string logFailureCode)
    {
        if (!result.LogWritten)
        {
            return [new ProjectProblem(logFailureCode, logPath)];
        }

        if (result.StartError is not null || result.ExitCode is null)
        {
            return [new ProjectProblem("dotnetUnavailable", logPath)];
        }

        return result.ExitCode == 0
            ? []
            : [new ProjectProblem(failureCode, logPath)];
    }

    internal static DotNetBuildResult RunDotNet(DotNetBuildCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = command.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (string argument in command.Arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            string output = standardOutput.Result;
            if (!string.IsNullOrEmpty(standardError.Result))
            {
                output += standardError.Result;
            }

            return new DotNetBuildResult(process.ExitCode, output, null);
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return new DotNetBuildResult(null, string.Empty, exception.Message);
        }
    }

    private static ProjectBuildReport Report(
        ModBuildTargetResolution resolution,
        string? log,
        IReadOnlyList<ProjectProblem> problems)
    {
        return new ProjectBuildReport(
            1,
            resolution.Inspection.Root,
            resolution.Inspection.Kind,
            resolution.Target is null
                ? null
                : RelativePath(resolution.Inspection.Root, resolution.Target.ProjectFile),
            Configuration,
            log,
            problems);
    }

    private static ModBuildTargetResolution Failure(
        ProjectInspectionReport inspection,
        string code)
    {
        return new ModBuildTargetResolution(
            inspection,
            null,
            [new ProjectProblem(code, null)]);
    }

    private static string FormatLog(DotNetBuildCommand command, DotNetBuildResult result)
    {
        var builder = new StringBuilder();
        builder.Append("dotnet");
        foreach (string argument in command.Arguments)
        {
            builder.Append(' ');
            builder.Append(argument);
        }

        builder.AppendLine();
        if (result.StartError is not null)
        {
            builder.AppendLine(result.StartError);
        }

        builder.Append(result.Output);
        return builder.ToString();
    }

    private static string RelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string FromSlashPath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string RelativeDirectory(string path)
    {
        int separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : path[..separatorIndex];
    }

    private static string WithTrailingSeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string EscapeMsBuildPropertyValue(string value)
    {
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("$", "%24", StringComparison.Ordinal)
            .Replace("@", "%40", StringComparison.Ordinal)
            .Replace("'", "%27", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal);
    }
}

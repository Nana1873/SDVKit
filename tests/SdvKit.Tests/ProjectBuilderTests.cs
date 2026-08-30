using System.Diagnostics;
using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class ProjectBuilderTests
{
    [Fact]
    public void GeneratedSmapiExampleBuildsAndPackagesWithTemporaryReferences()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary, "ExampleMod;safe");
        string gamePath = CreateReferenceAssemblies(temporary);
        Func<DoctorReport> doctor = () => new DoctorReport(
            1,
            DoctorReport.Ready,
            [new DetectedInstallation(gamePath)]);

        ProjectBuildReport build = ProjectBuilder.Build(target, doctor);
        ProjectPackageReport package = ProjectPackager.Package(target, doctor);

        Assert.True(
            build.Problems.Count == 0,
            File.ReadAllText(System.IO.Path.Combine(target, ".sdvkit", "logs", "build.log")));
        Assert.Empty(package.Problems);
        Assert.Equal(
            ["ExampleMod/ExampleMod.dll", "ExampleMod/manifest.json"],
            package.Entries);
        Assert.NotNull(package.Archive);
        Assert.True(File.Exists(System.IO.Path.Combine(
            target,
            package.Archive.Replace('/', System.IO.Path.DirectorySeparatorChar))));
        Assert.Single(ProjectArtifactDirectories(target, "bin", "ExampleMod"));
        Assert.True(File.Exists(System.IO.Path.Combine(
            Assert.Single(ProjectArtifactDirectories(target, "obj", "ExampleMod")),
            "project.assets.json")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, "bin")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, "obj")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(gamePath, "Mods")));
    }

    [Fact]
    public void BuildSeparatesProjectReferenceArtifacts()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <SdvKitOriginalPropsImported>true</SdvKitOriginalPropsImported>
              </PropertyGroup>
            </Project>
            """);
        temporary.WriteFile("Directory.Build.targets", """
            <Project>
              <Target Name="VerifyOriginalBuildFiles" BeforeTargets="CoreCompile">
                <Error Condition="'$(SdvKitOriginalPropsImported)' != 'true'" Text="Original Directory.Build.props was not imported." />
                <WriteLinesToFile File="$(BaseIntermediateOutputPath)original-targets.txt" Lines="imported" Overwrite="true" />
              </Target>
            </Project>
            """);
        string target = CreateMod(temporary);
        string sharedDirectory = AddExternalProjectReference(temporary, target);
        string gamePath = CreateReferenceAssemblies(temporary);

        ProjectBuildReport build = ProjectBuilder.Build(
            target,
            () => new DoctorReport(
                1,
                DoctorReport.Ready,
                [new DetectedInstallation(gamePath)]));

        Assert.True(
            build.Problems.Count == 0,
            File.ReadAllText(System.IO.Path.Combine(target, ".sdvkit", "logs", "build.log")));
        string[] intermediateDirectories = ProjectArtifactDirectories(
            target,
            "obj",
            "ExampleMod");
        Assert.Equal(2, intermediateDirectories.Length);
        Assert.All(
            intermediateDirectories,
            directory =>
            {
                Assert.True(File.Exists(System.IO.Path.Combine(
                    directory,
                    "project.assets.json")));
                Assert.True(File.Exists(System.IO.Path.Combine(
                    directory,
                    "original-targets.txt")));
            });
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, "bin")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, "obj")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(sharedDirectory, "bin")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(sharedDirectory, "obj")));
    }

    [Fact]
    public void BuildRejectsProjectOutputOverridesOutsideStateDirectory()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary);
        string projectPath = System.IO.Path.Combine(target, "ExampleMod.csproj");
        string contents = File.ReadAllText(projectPath).Replace(
            "</Project>",
            """
              <PropertyGroup>
                <BaseOutputPath>../outside/base/</BaseOutputPath>
                <OutputPath>../outside/output/</OutputPath>
                <OutDir>../outside/out/</OutDir>
                <BaseIntermediateOutputPath>../outside/base-obj/</BaseIntermediateOutputPath>
                <IntermediateOutputPath>../outside/obj/</IntermediateOutputPath>
                <MSBuildProjectExtensionsPath>../outside/extensions/</MSBuildProjectExtensionsPath>
              </PropertyGroup>
            </Project>
            """,
            StringComparison.Ordinal);
        File.WriteAllText(projectPath, contents);
        string gamePath = CreateReferenceAssemblies(temporary);

        ProjectBuildReport build = ProjectBuilder.Build(
            target,
            () => new DoctorReport(
                1,
                DoctorReport.Ready,
                [new DetectedInstallation(gamePath)]));

        ProjectProblem problem = Assert.Single(build.Problems);
        Assert.Equal("buildFailed", problem.Code);
        Assert.Equal(ProjectBuilder.BuildLogPath, problem.Path);
        Assert.Contains(
            "SDVKit output isolation rejected",
            File.ReadAllText(System.IO.Path.Combine(target, ".sdvkit", "logs", "build.log")),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(System.IO.Path.Combine(temporary.Path, "outside")));
    }

    [Fact]
    public void BuildUsesDotNetArtifactsAndDisablesDeployAndZip()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary);
        DotNetBuildCommand? observed = null;

        ProjectBuildReport report = ProjectBuilder.Build(
            target,
            ReadyDoctor,
            command =>
            {
                observed = command;
                return new DotNetBuildResult(0, "build succeeded", null);
            });

        Assert.Empty(report.Problems);
        Assert.Equal("ExampleMod.csproj", report.ProjectFile);
        Assert.Equal("Release", report.Configuration);
        Assert.Equal(".sdvkit/logs/build.log", report.Log);
        Assert.NotNull(observed);
        Assert.Equal(target, observed.WorkingDirectory);
        Assert.Equal("build", observed.Arguments[0]);
        string propsPath = System.IO.Path.Combine(
            target,
            ".sdvkit",
            "build",
            "sdvkit.build.props");
        Assert.Contains(
            $"-p:DirectoryBuildPropsPath={propsPath}",
            observed.Arguments);
        Assert.Contains("-p:ImportDirectoryBuildProps=true", observed.Arguments);
        string targetsPath = System.IO.Path.Combine(
            target,
            ".sdvkit",
            "build",
            "sdvkit.build.targets");
        Assert.Contains(
            $"-p:DirectoryBuildTargetsPath={targetsPath}",
            observed.Arguments);
        Assert.Contains("-p:ImportDirectoryBuildTargets=true", observed.Arguments);
        Assert.Contains("-p:UseArtifactsOutput=false", observed.Arguments);
        string props = File.ReadAllText(propsPath);
        Assert.Contains("<Import Project=\"$(_SdvKitOriginalProps)\"", props, StringComparison.Ordinal);
        Assert.Contains("$([MSBuild]::StableStringHash(&quot;$(MSBuildProjectFullPath)&quot;))", props, StringComparison.Ordinal);
        Assert.Contains(
            $"<BaseOutputPath>{System.IO.Path.Combine(target, ".sdvkit", "build", "bin")}{System.IO.Path.DirectorySeparatorChar}$(_SdvKitProjectKey){System.IO.Path.DirectorySeparatorChar}</BaseOutputPath>",
            props,
            StringComparison.Ordinal);
        Assert.Contains(
            $"<BaseIntermediateOutputPath>{System.IO.Path.Combine(target, ".sdvkit", "build", "obj")}{System.IO.Path.DirectorySeparatorChar}$(_SdvKitProjectKey){System.IO.Path.DirectorySeparatorChar}</BaseIntermediateOutputPath>",
            props,
            StringComparison.Ordinal);
        string targets = File.ReadAllText(targetsPath);
        Assert.Contains(
            "<Project InitialTargets=\"_SdvKitValidateOutputIsolation\">",
            targets,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Import Project=\"$(_SdvKitOriginalTargets)\"",
            targets,
            StringComparison.Ordinal);
        Assert.Contains("<FindUnderPath", targets, StringComparison.Ordinal);
        Assert.DoesNotContain(
            observed.Arguments,
            argument => argument.StartsWith("-p:BaseOutputPath=", StringComparison.Ordinal)
                || argument.StartsWith("-p:BaseIntermediateOutputPath=", StringComparison.Ordinal));
        Assert.DoesNotContain("--artifacts-path", observed.Arguments);
        Assert.Contains("-p:EnableModDeploy=false", observed.Arguments);
        Assert.Contains("-p:EnableModZip=false", observed.Arguments);
        Assert.Contains("-p:GamePath=C:\\Game", observed.Arguments);
        Assert.Equal("-p:EnableModDeploy=false", observed.Arguments[^1]);
        Assert.DoesNotContain(
            observed.Arguments,
            argument => argument.Contains("Mods", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "build succeeded",
            File.ReadAllText(System.IO.Path.Combine(target, ".sdvkit", "logs", "build.log")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ContentPackIsRejectedBeforeDiscoveryOrProcessStart()
    {
        using TemporaryDirectory temporary = new();
        string target = System.IO.Path.Combine(temporary.Path, "ExamplePack");
        ProjectCreator.Create(new ProjectCreationRequest(
            ProjectCreator.ContentPack,
            target,
            "Pack",
            "Nana",
            "Nana.Pack",
            "Example."));

        ProjectBuildReport report = ProjectBuilder.Build(
            target,
            () => throw new InvalidOperationException("Discovery should not run."),
            _ => throw new InvalidOperationException("dotnet should not run."));

        Assert.Equal("projectNotBuildable", Assert.Single(report.Problems).Code);
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit")));
    }

    [Fact]
    public void AmbiguousGameInstallationIsAControlledOutcome()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary);

        ProjectBuildReport report = ProjectBuilder.Build(
            target,
            () => new DoctorReport(
                1,
                DoctorReport.Ambiguous,
                [new DetectedInstallation("C:\\One"), new DetectedInstallation("D:\\Two")]),
            _ => throw new InvalidOperationException("dotnet should not run."));

        Assert.Equal("gameInstallationAmbiguous", Assert.Single(report.Problems).Code);
        Assert.Null(report.Log);
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit")));
    }

    [Fact]
    public void UnrelatedProjectFileCannotBeBuiltForANestedManifest()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        temporary.WriteFile("mod/manifest.json", """
            {
              "Name": "Example",
              "Author": "Nana",
              "UniqueID": "Nana.Example",
              "Version": "1.0.0",
              "Description": "Example.",
              "EntryDll": "Example.dll"
            }
            """);

        ProjectBuildReport report = ProjectBuilder.Build(
            temporary.Path,
            () => throw new InvalidOperationException("Discovery should not run."),
            _ => throw new InvalidOperationException("dotnet should not run."));

        Assert.Equal("projectManifestMismatch", Assert.Single(report.Problems).Code);
        Assert.False(Directory.Exists(System.IO.Path.Combine(temporary.Path, ".sdvkit")));
    }

    [Fact]
    public void BuildFailurePointsToTheIsolatedLog()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary);

        ProjectBuildReport report = ProjectBuilder.Build(
            target,
            ReadyDoctor,
            _ => new DotNetBuildResult(1, "compiler error", null));

        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("buildFailed", problem.Code);
        Assert.Equal(".sdvkit/logs/build.log", problem.Path);
        Assert.Contains(
            "compiler error",
            File.ReadAllText(System.IO.Path.Combine(target, ".sdvkit", "logs", "build.log")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MsBuildPropertyValuesCannotReenableDeployment()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary);
        DotNetBuildCommand? observed = null;

        ProjectBuildReport report = ProjectBuilder.Build(
            target,
            () => new DoctorReport(
                1,
                DoctorReport.Ready,
                [new DetectedInstallation("C:\\Game;EnableModDeploy=true")]),
            command =>
            {
                observed = command;
                return new DotNetBuildResult(0, string.Empty, null);
            });

        Assert.Empty(report.Problems);
        Assert.NotNull(observed);
        Assert.Contains("-p:GamePath=C:\\Game%3BEnableModDeploy=true", observed.Arguments);
        Assert.DoesNotContain(
            observed.Arguments,
            argument => argument.Contains(";EnableModDeploy", StringComparison.Ordinal));
        Assert.Equal("-p:EnableModDeploy=false", observed.Arguments[^1]);
    }

    [Fact]
    public void UnsafeStatePathIsRejectedBeforeProcessStart()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateMod(temporary);
        File.WriteAllText(System.IO.Path.Combine(target, ".sdvkit"), "not a state directory");

        ProjectBuildReport report = ProjectBuilder.Build(
            target,
            ReadyDoctor,
            _ => throw new InvalidOperationException("dotnet should not run."));

        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("unsafeStateDirectory", problem.Code);
        Assert.Equal(".sdvkit", problem.Path);
    }

    private static string CreateMod(
        TemporaryDirectory temporary,
        string directoryName = "ExampleMod")
    {
        string target = System.IO.Path.Combine(temporary.Path, directoryName);
        ProjectCreationReport created = ProjectCreator.Create(new ProjectCreationRequest(
            ProjectCreator.SmapiMod,
            target,
            "Example mod",
            "Nana",
            "Nana.ExampleMod",
            "Example."));
        Assert.Empty(created.Problems);
        return target;
    }

    private static DoctorReport ReadyDoctor()
    {
        return new DoctorReport(1, DoctorReport.Ready, [new DetectedInstallation("C:\\Game")]);
    }

    private static string AddExternalProjectReference(
        TemporaryDirectory temporary,
        string target)
    {
        string sharedDirectory = System.IO.Path.Combine(temporary.Path, "Shared");
        Directory.CreateDirectory(sharedDirectory);
        string sharedProject = System.IO.Path.Combine(sharedDirectory, "ExampleMod.csproj");
        File.WriteAllText(sharedProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net6.0</TargetFramework>
                <AssemblyName>Shared</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        string modProject = System.IO.Path.Combine(target, "ExampleMod.csproj");
        string projectReference = System.IO.Path.GetRelativePath(target, sharedProject);
        string contents = File.ReadAllText(modProject).Replace(
            "</Project>",
            $"""
              <ItemGroup>
                <ProjectReference Include="{projectReference}" />
              </ItemGroup>
            </Project>
            """,
            StringComparison.Ordinal);
        File.WriteAllText(modProject, contents);
        return sharedDirectory;
    }

    private static string[] ProjectArtifactDirectories(
        string target,
        string kind,
        string projectName)
    {
        string root = System.IO.Path.Combine(target, ".sdvkit", "build", kind);
        return Directory.GetDirectories(
            root,
            $"{projectName}-*",
            SearchOption.TopDirectoryOnly);
    }

    private static string CreateReferenceAssemblies(TemporaryDirectory temporary)
    {
        string sourcePath = System.IO.Path.Combine(temporary.Path, "reference-source");
        string buildOutputPath = System.IO.Path.Combine(temporary.Path, "reference-output");
        string gamePath = System.IO.Path.Combine(temporary.Path, "reference-game (safe);refs");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(buildOutputPath);
        File.WriteAllText(System.IO.Path.Combine(sourcePath, "StardewModdingAPI.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net6.0</TargetFramework>
                <AssemblyName>StardewModdingAPI</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(System.IO.Path.Combine(sourcePath, "Api.cs"), """
            namespace StardewModdingAPI;

            public interface IModHelper
            {
            }

            public abstract class Mod
            {
                public abstract void Entry(IModHelper helper);
            }
            """);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = sourcePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "build",
            "StardewModdingAPI.csproj",
            "--configuration",
            "Release",
            "--output",
            buildOutputPath,
            "--nologo",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);

        File.Copy(
            System.IO.Path.Combine(buildOutputPath, "StardewModdingAPI.dll"),
            System.IO.Path.Combine(buildOutputPath, "Stardew Valley.dll"));
        Directory.Move(buildOutputPath, gamePath);
        return gamePath;
    }
}

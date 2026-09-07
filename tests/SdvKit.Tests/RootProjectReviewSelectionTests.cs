using System.IO.Compression;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using DotNetBuildCommand = SdvKit.Cli.DotNetBuildCommand;

namespace SdvKit.Tests;

public sealed class RootProjectReviewSelectionTests
{
    [Theory]
    [InlineData("single", false)]
    [InlineData("single", true)]
    [InlineData("network-2", false)]
    [InlineData("network-2", true)]
    public void ExplicitRootProjectReviewsOnlyItsPackageAndSelectedCompanions(string topology, bool includeCompanion)
    {
        using TemporaryDirectory temporary = new();
        string root = CreateRootWithTestProjects(temporary);
        string companion = Path.Combine(root, "tests", "LiveHarness");
        string[] companions = includeCompanion ? [companion] : [];
        string before = ModBuildIdentity.ComputeFileSet(root);
        LiveLabPaths paths = ResolveLab(temporary);
        var commands = new List<DotNetBuildCommand>();

        ProjectReviewPreparationResult prepared = ProjectModStager.PrepareReview(
            root, companions, [], paths, Ready,
            command =>
            {
                commands.Add(command);
                return PackageRunner(command);
            }, "RootMod.csproj");

        try
        {
            Assert.Null(prepared.Problem);
            string[] expectedProjects = includeCompanion
                ? [Path.Combine(root, "RootMod.csproj"), Path.Combine(root, "RootMod.csproj"), Path.Combine(companion, "LiveHarness.csproj"), Path.Combine(companion, "LiveHarness.csproj")]
                : [Path.Combine(root, "RootMod.csproj"), Path.Combine(root, "RootMod.csproj")];
            Assert.Equal(expectedProjects, commands.Select(command => command.Arguments[1]));
            Assert.All(commands, command =>
            {
                Assert.Equal(Path.GetDirectoryName(command.Arguments[1]), command.WorkingDirectory);
                Assert.Contains("-p:EnableModDeploy=false", command.Arguments);
            });
            Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
            Assert.False(Directory.Exists(Path.Combine(root, ".sdvkit")));

            Assert.Equal(includeCompanion ? 2 : 1, prepared.Artifacts.Count);
            ProjectReviewPreparedArtifact target = Assert.Single(prepared.Artifacts, artifact => artifact.Role == ProjectReviewArtifactRole.Target);
            Assert.Equal("Tests.RootMod", target.Manifest.UniqueId);
            Assert.Equal(Path.Combine(root, "RootMod.csproj"), target.ProjectFile);
            Assert.False(Directory.Exists(Path.Combine(target.PreparedPath, "tests")));

            ProjectReviewStagingResult staged = ProjectModStager.StageReview(prepared.Artifacts, topology, paths, gamePath: "C:\\SelectedGame");
            Assert.Null(staged.Problem);
            ProjectReviewStagingResult read = ProjectModStager.ReadReview(paths, topology);
            Assert.Null(read.Problem);
            Assert.NotNull(read.Staging);
            Assert.Equal("Tests.RootMod", read.Staging.Target.Manifest.UniqueId);
            Assert.Equal(Path.Combine(root, "RootMod.csproj"), read.Staging.Target.ProjectFile);
            Assert.Equal("C:\\SelectedGame", read.Staging.GamePath);
            Assert.Equal(includeCompanion ? 2 : 1, read.Staging.Artifacts.Count);
            if (includeCompanion)
            {
                ProjectReviewOwnedArtifact stagedCompanion = Assert.Single(read.Staging.Artifacts, artifact => artifact.Role == ProjectReviewArtifactRole.Companion);
                Assert.Equal("Tests.LiveHarness", stagedCompanion.Manifest.UniqueId);
                Assert.Null(stagedCompanion.ProjectFile);
            }
            foreach (ProjectReviewOwnedArtifact artifact in read.Staging.Artifacts)
            {
                Assert.Equal(topology == "single" ? 1 : 2, artifact.RoleStagingPaths.Count);
                foreach (ProjectReviewRoleStagingPath rolePath in artifact.RoleStagingPaths)
                {
                    Assert.Equal(artifact.BuildIdentity, ModBuildIdentity.ComputeFileSet(rolePath.StagingPath));
                    Assert.Equal(2, Directory.GetFiles(rolePath.StagingPath, "*", SearchOption.AllDirectories).Length);
                }
            }
        }
        finally
        {
            Assert.True(ProjectModStager.RemoveReview(paths, topology).Removed);
            Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
        }

        ProjectReviewStagingResult cleared = ProjectModStager.ReadReview(paths, topology);
        Assert.Null(cleared.Problem);
        Assert.Null(cleared.Staging);
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    [Fact]
    public void RootWithTestProjectsStillRequiresAnExplicitSelection()
    {
        using TemporaryDirectory temporary = new();
        string root = CreateRootWithTestProjects(temporary);
        string before = ModBuildIdentity.ComputeFileSet(root);
        LiveLabPaths paths = ResolveLab(temporary);

        ProjectReviewPreparationResult prepared = ProjectModStager.PrepareReview(
            root, [], [], paths,
            () => throw new InvalidOperationException("Ambiguous selection must not discover an installation."),
            _ => throw new InvalidOperationException("Ambiguous selection must not build."));

        Assert.Equal("projectFileAmbiguous", prepared.Problem?.Code);
        Assert.Empty(prepared.Artifacts);
        Assert.Null(prepared.PreparationRoot);
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    [Theory]
    [InlineData("nestedManifest", "reviewPackageInvalid")]
    [InlineData("wrongManifest", "unsafePackageArchive")]
    public void ExplicitRootProjectStillRejectsAnInvalidReleasePackage(string packageShape, string expectedProblem)
    {
        using TemporaryDirectory temporary = new();
        string root = CreateRootWithTestProjects(temporary);
        string before = ModBuildIdentity.ComputeFileSet(root);
        LiveLabPaths paths = ResolveLab(temporary);
        int builds = 0;

        ProjectReviewPreparationResult prepared = ProjectModStager.PrepareReview(
            root, [], [], paths, Ready,
            command =>
            {
                builds++;
                return PackageRunner(command, packageShape);
            }, "RootMod.csproj");

        Assert.Equal(expectedProblem, prepared.Problem?.Code);
        Assert.Equal(2, builds);
        Assert.Empty(prepared.Artifacts);
        Assert.Null(prepared.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(paths.SingleRoot, "review-prepared")));
        Assert.Null(ProjectModStager.ReadReview(paths).Staging);
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    private static string CreateRootWithTestProjects(TemporaryDirectory temporary)
    {
        string root = Path.Combine(temporary.Path, "sources", "RootMod");
        CreateMod(root, "RootMod");
        temporary.WriteFile("sources/RootMod/tests/Core/Core.csproj", "<Project />");
        CreateMod(Path.Combine(root, "tests", "LiveHarness"), "LiveHarness");
        return root;
    }

    private static void CreateMod(string path, string name)
    {
        Assert.Empty(ProjectCreator.Create(new(
            ProjectCreator.SmapiMod, path, name, "Tests", $"Tests.{name}", "Original root-project selection test mod.")).Problems);
    }

    private static LiveLabPaths ResolveLab(TemporaryDirectory temporary)
        => LiveLabPaths.Resolve(Directory.CreateDirectory(Path.Combine(temporary.Path, "lab")).FullName);

    private static DoctorReport Ready() => new(1, DoctorReport.Ready, [new("C:\\SelectedGame")]);

    private static DotNetBuildResult PackageRunner(DotNetBuildCommand command, string? packageShape = null)
    {
        if (command.Arguments.Contains("-p:EnableModZip=true"))
        {
            string root = Path.GetDirectoryName(command.Arguments[1])!;
            string name = Path.GetFileNameWithoutExtension(command.Arguments[1]);
            string zipPath = command.Arguments.Single(argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal))["-p:ModZipPath=".Length..];
            string manifest = File.ReadAllText(Path.Combine(root, "manifest.json"));
            string? harnessManifest = packageShape is null
                ? null
                : File.ReadAllText(Path.Combine(root, "tests", "LiveHarness", "manifest.json"));
            using ZipArchive archive = ZipFile.Open(Path.Combine(zipPath, name + ".zip"), ZipArchiveMode.Create);
            WriteEntry(archive, $"{name}/manifest.json", packageShape == "wrongManifest" ? harnessManifest! : manifest);
            WriteEntry(archive, $"{name}/{name}.dll", "selected built assembly");
            if (packageShape == "nestedManifest")
            {
                WriteEntry(archive, $"{name}/tests/LiveHarness/manifest.json", harnessManifest!);
                WriteEntry(archive, $"{name}/tests/LiveHarness/LiveHarness.dll", "unselected built assembly");
            }
        }
        return new(0, "ok", null);
    }

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        using StreamWriter writer = new(archive.CreateEntry(path).Open());
        writer.Write(value);
    }
}

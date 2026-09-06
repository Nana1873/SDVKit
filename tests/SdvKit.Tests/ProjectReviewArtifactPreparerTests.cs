using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewArtifactPreparerTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("network-2")]
    public void ReadyCodeTargetStagesExactBytesWithoutBuildingAndKeepsSourceAfterCleanup(string topology)
    {
        using TemporaryDirectory temporary = new();
        string target = WriteReadyCodeMod(temporary.Path, "PackagedMod", "Test.PackagedMod", "1.0.0");
        File.WriteAllBytes(Path.Combine(target, "Provider.dll"), [0, 1, 2, 255]);
        string[] before = SnapshotTree(target);
        LiveLabPaths paths = ResolveLab(temporary.Path);

        var prepared = ProjectModStager.PrepareReview(target, [], [], paths, DoctorMustNotRun,
            _ => throw new InvalidOperationException("Ready targets must not build or package."));

        Assert.Null(prepared.Problem);
        var artifact = Assert.Single(prepared.Artifacts);
        Assert.Equal(ProjectReviewArtifactRole.Target, artifact.Role);
        Assert.Equal(ProjectInspectionReport.SmapiMod, artifact.Manifest.Kind);
        Assert.Null(artifact.ProjectFile);
        Assert.Equal(before, SnapshotTree(artifact.PreparedPath));
        Assert.Equal(ModBuildIdentity.ComputeFileSet(target), artifact.BuildIdentity);
        var staged = ProjectModStager.StageReview(prepared.Artifacts, topology, paths);
        Assert.Null(staged.Problem);
        Assert.Equal(topology == "single" ? 1 : 2, staged.Staging!.Target.RoleStagingPaths.Count);
        Assert.All(staged.Staging.Target.RoleStagingPaths,
            role => Assert.Equal(before, SnapshotTree(role.StagingPath)));
        Assert.Equal(artifact.BuildIdentity, staged.Staging.Target.BuildIdentity);
        Assert.True(ProjectModStager.RemoveReview(paths, topology).Removed);
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
        Assert.Equal(before, SnapshotTree(target));
    }

    [Theory]
    [InlineData("missingDll", "reviewReadyManifestInvalid")]
    [InlineData("invalidEntryDll", "invalidManifest")]
    [InlineData("bundle", "reviewReadyDirectoryInvalid")]
    [InlineData("sourceFile", "reviewPreparationFailed")]
    public void ReadyCodeTargetRejectsInvalidSelectionWithoutChangingSource(string mutation, string expectedCode)
    {
        using TemporaryDirectory temporary = new();
        string target = WriteReadyCodeMod(temporary.Path, "PackagedMod", "Test.PackagedMod", "1.0.0");
        switch (mutation)
        {
            case "missingDll": File.Delete(Path.Combine(target, "Provider.dll")); break;
            case "invalidEntryDll":
                File.WriteAllText(Path.Combine(target, "manifest.json"),
                    Manifest("Test.PackagedMod", "1.0.0", entryDll: "../Provider.dll"));
                break;
            case "bundle": WriteReadyCodeMod(target, "Nested", "Test.Nested", "1.0.0"); break;
            case "sourceFile": File.WriteAllText(Path.Combine(target, "Source.cs"), "source"); break;
        }
        string[] before = SnapshotTree(target);
        LiveLabPaths paths = ResolveLab(temporary.Path);
        var prepared = ProjectModStager.PrepareReview(target, [], [], paths, DoctorMustNotRun);
        Assert.Equal(expectedCode, prepared.Problem?.Code);
        Assert.Null(prepared.PreparationRoot);
        Assert.Empty(prepared.Artifacts);
        Assert.Equal(before, SnapshotTree(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ReadyCodeTargetDoesNotIgnoreExplicitProjectSelector()
    {
        using TemporaryDirectory temporary = new();
        string target = WriteReadyCodeMod(temporary.Path, "PackagedMod", "Test.PackagedMod", "1.0.0");
        string[] before = SnapshotTree(target);
        var prepared = ProjectModStager.PrepareReview(target, [], [], ResolveLab(temporary.Path),
            DoctorMustNotRun, projectFile: "Missing.csproj");
        Assert.Equal("projectSelectionInvalid", prepared.Problem?.Code);
        Assert.Null(prepared.PreparationRoot);
        Assert.Empty(prepared.Artifacts);
        Assert.Equal(before, SnapshotTree(target));
    }

    [Fact]
    public void GeneratedCpPackRemainsReviewableAfterCheckAndPackage()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "Pack");
        Assert.Empty(ProjectCreator.Create(new ProjectCreationRequest(ProjectCreator.ContentPack,
            target, "Pack", "SDVKit", "SDVKit.Pack", "Original generated pack.")).Problems);
        Assert.Empty(ProjectChecker.Check(target).Problems);
        Assert.Empty(ProjectPackager.Package(target, DoctorMustNotRun).Problems);
        string[] before = SnapshotTree(target);
        string provider = WriteReadyCodeMod(temporary.Path, "Provider", "Pathoschild.ContentPatcher", "2.9.1");
        LiveLabPaths paths = ResolveLab(temporary.Path);
        var prepared = ProjectModStager.PrepareReview(target, [provider], [], paths, DoctorMustNotRun);
        Assert.Null(prepared.Problem);
        var artifact = prepared.Artifacts.Single(a => a.Role == ProjectReviewArtifactRole.Target);
        Assert.False(Directory.Exists(Path.Combine(artifact.PreparedPath, ".sdvkit")));
        Assert.Equal(artifact.BuildIdentity, ProjectModStager.ComputeCpSourceIdentity(target));
        Assert.NotEqual(artifact.BuildIdentity, ModBuildIdentity.ComputeFileSet(target));
        var staged = ProjectModStager.StageReview(prepared.Artifacts, paths);
        Assert.Null(staged.Problem);
        Assert.Equal(artifact.BuildIdentity, ModBuildIdentity.ComputeFileSet(staged.Staging!.Target.StagingPath));
        Assert.True(ProjectModStager.RemoveReview(paths).Removed);
        Assert.Equal(before, SnapshotTree(target));
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
    }

    [Theory]
    [InlineData(".sdvkit/packages/Pack.zip", true)]
    [InlineData(".SDVKIT/packages/Pack.zip", true)]
    [InlineData("assets/.sdvkit/payload.json", false)]
    [InlineData("assets/.SDVKIT/payload.json", false)]
    [InlineData("assets/Saves/payload.json", false)]
    [InlineData("assets/SaveGameInfo", false)]
    [InlineData("assets/Stardew Valley.dll", false)]
    [InlineData("assets/source.cs", false)]
    [InlineData("assets/other.zip", false)]
    [InlineData("obj/output.json", false)]
    public void CpSourceOutputExceptionIsRootOnlyAndKeepsUnsafePayloadRejection(string payload, bool accepted)
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(temporary.Path, "Pack", "Test.Pack", "1.0.0", "Pathoschild.ContentPatcher");
        string provider = WriteReadyCodeMod(temporary.Path, "Provider", "Pathoschild.ContentPatcher", "2.9.1");
        temporary.WriteFile("Pack/" + payload, "payload");
        string[] before = SnapshotTree(target);
        LiveLabPaths paths = ResolveLab(temporary.Path);
        var prepared = ProjectModStager.PrepareReview(target, [provider], [], paths, DoctorMustNotRun);
        Assert.Equal(accepted, prepared.Problem is null);
        if (accepted)
        {
            var artifact = prepared.Artifacts.Single(a => a.Role == ProjectReviewArtifactRole.Target);
            Assert.Equal(artifact.BuildIdentity, ProjectModStager.ComputeCpSourceIdentity(target));
            Assert.False(File.Exists(Path.Combine(artifact.PreparedPath, payload)));
        }
        else Assert.Throws<InvalidDataException>(() => ProjectModStager.ComputeCpSourceIdentity(target));
        Assert.Equal(before, SnapshotTree(target));
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("additionalPack")]
    [InlineData("otherProvider")]
    public void OutputExceptionDoesNotApplyToCompanionsOrOtherProviders(string context)
    {
        using TemporaryDirectory temporary = new();
        string providerId = context == "otherProvider" ? "Test.Provider" : "Pathoschild.ContentPatcher";
        string target = WriteContentPack(temporary.Path, "Pack", "Test.Pack", "1.0.0", providerId);
        string provider = WriteReadyCodeMod(temporary.Path, "Provider", providerId, "2.9.1");
        string additional = WriteContentPack(temporary.Path, "Additional", "Test.Additional", "1.0.0", providerId);
        string outputRoot = context == "provider" ? provider : context == "additionalPack" ? additional : target;
        Directory.CreateDirectory(Path.Combine(outputRoot, ".sdvkit", "packages"));
        var prepared = ProjectModStager.PrepareReview(target, [provider], [additional], ResolveLab(temporary.Path), DoctorMustNotRun);
        Assert.NotNull(prepared.Problem);
    }

    [Fact]
    public void CodeProjectPreparationKeepsExternalSourceReadOnlyAndOwnsAllBuildOutputs()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "external-source", "CodeTarget");
        ProjectCreationReport created = ProjectCreator.Create(new ProjectCreationRequest(
            ProjectCreator.SmapiMod,
            target,
            "Code target",
            "Nana",
            "Nana.CodeTarget",
            "External read-only review target."));
        Assert.Empty(created.Problems);
        string[] sourceBefore = SnapshotTree(target);
        LiveLabPaths paths = ResolveLab(Path.Combine(temporary.Path, "sdvkit-owner"));
        var commands = new List<SdvKit.Cli.DotNetBuildCommand>();

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            ReadyDoctor,
            command =>
            {
                commands.Add(command);
                if (command.Arguments.Contains(
                        "-p:EnableModZip=true",
                        StringComparer.Ordinal))
                {
                    string zipPath = command.Arguments
                        .Single(argument => argument.StartsWith(
                            "-p:ModZipPath=",
                            StringComparison.Ordinal))["-p:ModZipPath=".Length..];
                    Directory.CreateDirectory(zipPath);
                    using ZipArchive archive = ZipFile.Open(
                        Path.Combine(zipPath, "CodeTarget 1.0.0.zip"),
                        ZipArchiveMode.Create);
                    WriteEntry(
                        archive,
                        "CodeTarget/manifest.json",
                        File.ReadAllText(Path.Combine(target, "manifest.json")));
                    WriteEntry(
                        archive,
                        "CodeTarget/CodeTarget.dll",
                        "review assembly");
                }

                return new DotNetBuildResult(0, "review build succeeded", null);
            });

        Assert.Null(result.Problem);
        string preparationRoot = Assert.IsType<string>(result.PreparationRoot);
        ProjectReviewPreparedArtifact artifact = Assert.Single(result.Artifacts);
        Assert.Equal(sourceBefore, SnapshotTree(target));
        Assert.False(Directory.Exists(Path.Combine(target, ".sdvkit")));
        Assert.False(Directory.Exists(Path.Combine(target, "bin")));
        Assert.False(Directory.Exists(Path.Combine(target, "obj")));
        Assert.Equal(2, commands.Count);
        Assert.All(commands, command =>
        {
            Assert.Equal(target, command.WorkingDirectory);
            Assert.All(
                command.Arguments.Where(argument =>
                    argument.StartsWith("-p:DirectoryBuildPropsPath=", StringComparison.Ordinal)
                    || argument.StartsWith("-p:DirectoryBuildTargetsPath=", StringComparison.Ordinal)
                    || argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal)),
                argument => Assert.True(IsBelow(
                    preparationRoot,
                    argument[(argument.IndexOf('=') + 1)..]),
                    argument));
        });
        string buildLog = Assert.IsType<string>(artifact.BuildLog);
        string packageLog = Assert.IsType<string>(artifact.PackageLog);
        Assert.True(IsBelow(preparationRoot, buildLog));
        Assert.True(IsBelow(preparationRoot, packageLog));
        Assert.True(File.Exists(buildLog));
        Assert.True(File.Exists(packageLog));
        Assert.All(
            Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories)
                .Where(path => !IsBelow(target, path)),
            path => Assert.True(IsBelow(preparationRoot, path), path));
        Assert.True(ProjectModStager.RemoveReviewPreparation(preparationRoot, paths));
        Assert.Equal(sourceBefore, SnapshotTree(target));
    }

    [Fact]
    public void CodeProjectPreparationRejectsReparsePointCreatedByBuildRunner()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "external-source", "CodeTarget");
        ProjectCreationReport created = ProjectCreator.Create(new ProjectCreationRequest(
            ProjectCreator.SmapiMod,
            target,
            "Code target",
            "Nana",
            "Nana.CodeTarget",
            "External read-only review target."));
        Assert.Empty(created.Problems);

        string outside = Path.Combine(temporary.Path, "outside-sentinel");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "unchanged");
        string outsideArchive = Path.Combine(outside, "CodeTarget 1.0.0.zip");
        using (ZipArchive archive = ZipFile.Open(outsideArchive, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "CodeTarget/manifest.json",
                File.ReadAllText(Path.Combine(target, "manifest.json")));
            WriteEntry(archive, "CodeTarget/CodeTarget.dll", "outside assembly");
        }

        string probe = Path.Combine(temporary.Path, "reparse-probe");
        try
        {
            Directory.CreateSymbolicLink(probe, outside);
            Directory.Delete(probe, recursive: false);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        string[] sourceBefore = SnapshotTree(target);
        string[] outsideBefore = SnapshotTree(outside);
        LiveLabPaths paths = ResolveLab(Path.Combine(temporary.Path, "sdvkit-owner"));
        string? replacedStagingPath = null;

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            ReadyDoctor,
            command =>
            {
                if (command.Arguments.Contains(
                        "-p:EnableModZip=true",
                        StringComparer.Ordinal))
                {
                    replacedStagingPath = command.Arguments
                        .Single(argument => argument.StartsWith(
                            "-p:ModZipPath=",
                            StringComparison.Ordinal))["-p:ModZipPath=".Length..];
                    Directory.Delete(replacedStagingPath, recursive: false);
                    Directory.CreateSymbolicLink(replacedStagingPath, outside);
                }

                return new DotNetBuildResult(0, "review build succeeded", null);
            });

        ProjectReviewProblem problem = Assert.IsType<ProjectReviewProblem>(result.Problem);
        Assert.Equal("packageLogUnavailable", problem.Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(result.Artifacts);
        Assert.NotNull(replacedStagingPath);
        Assert.False(Directory.Exists(replacedStagingPath));
        Assert.Equal(sourceBefore, SnapshotTree(target));
        Assert.Equal(outsideBefore, SnapshotTree(outside));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.True(File.Exists(outsideArchive));
        Assert.False(File.Exists(Path.Combine(outside, "package.log")));
    }

    [Fact]
    public void ContentPackTargetWithExplicitProviderPreparesAndStagesSingle()
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            temporary.Path,
            "TargetPack",
            "Nana.TargetPack",
            "1.0",
            "Nana.Provider",
            "2.0.0");
        string provider = WriteReadyCodeMod(
            temporary.Path,
            "Provider",
            "Nana.Provider",
            "2.1.0");
        string additional = WriteContentPack(
            temporary.Path,
            "AdditionalPack",
            "Nana.AdditionalPack",
            "1.0.0",
            "Nana.Provider");
        LiveLabPaths paths = ResolveLab(temporary.Path);
        var doctorCalled = false;

        ProjectReviewPreparationResult prepared = ProjectModStager.PrepareReview(
            target,
            [provider],
            [additional],
            paths,
            () =>
            {
                doctorCalled = true;
                return new DoctorReport(1, DoctorReport.NotFound, []);
            });

        Assert.Null(prepared.Problem);
        Assert.False(doctorCalled);
        ProjectReviewPreparedArtifact preparedTarget = prepared.Artifacts.Single(artifact =>
            artifact.Role == ProjectReviewArtifactRole.Target);
        Assert.Equal(ProjectInspectionReport.ContentPack, preparedTarget.Manifest.Kind);
        Assert.Equal("Nana.TargetPack", preparedTarget.Manifest.UniqueId);
        Assert.Equal("1.0", preparedTarget.Manifest.Version);
        Assert.Equal("Nana.Provider", preparedTarget.Manifest.ContentPackFor);
        Assert.Contains(prepared.Artifacts, artifact =>
            artifact.Role == ProjectReviewArtifactRole.Companion
            && artifact.Manifest.UniqueId == "Nana.Provider");
        Assert.Contains(prepared.Artifacts, artifact =>
            artifact.Role == ProjectReviewArtifactRole.ContentPack
            && artifact.Manifest.UniqueId == "Nana.AdditionalPack");

        ProjectReviewStagingResult staged = ProjectModStager.StageReview(
            prepared.Artifacts,
            paths);

        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(staged.Staging);
        Assert.Null(staged.Problem);
        Assert.Equal("Nana.TargetPack", staging.TargetLaunchState.UniqueId);
        Assert.Equal("1.0.0", staging.TargetLaunchState.Version);
        Assert.Equal(preparedTarget.BuildIdentity, staging.TargetLaunchState.BuildIdentity);
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);
        Assert.True(cleanup.Removed);
        Assert.Null(cleanup.Problem);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Theory]
    [InlineData(false, "Nana.Provider")]
    [InlineData(true, "Nana.WrongProvider")]
    public void ContentPackTargetRequiresItsExactExplicitProvider(
        bool includeCompanion,
        string companionUniqueId)
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            temporary.Path,
            "TargetPack",
            "Nana.TargetPack",
            "1.0.0",
            "Nana.Provider");
        IReadOnlyList<string> companions = includeCompanion
            ? [WriteReadyCodeMod(
                temporary.Path,
                "Companion",
                companionUniqueId,
                "1.0.0")]
            : [];
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            companions,
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "reviewDependencyUnavailable",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.False(File.Exists(Path.Combine(paths.SingleRoot, "project-review-staging.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Theory]
    [InlineData("1.9.9", "reviewDependencyVersionMismatch")]
    [InlineData("2.0.0", null)]
    public void ContentPackTargetEnforcesProviderMinimumVersion(
        string providerVersion,
        string? expectedProblem)
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            temporary.Path,
            "TargetPack",
            "Nana.TargetPack",
            "1.0.0",
            "Nana.Provider",
            "2.0.0");
        string provider = WriteReadyCodeMod(
            temporary.Path,
            "Provider",
            "Nana.Provider",
            providerVersion);
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [provider],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(expectedProblem, result.Problem?.Code);
        Assert.True(ProjectModStager.RemoveReviewPreparation(result.PreparationRoot, paths));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsAnInvalidHybridManifest()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "HybridPack");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, "manifest.json"),
            Manifest(
                "Nana.Hybrid",
                "1.0.0",
                entryDll: "Hybrid.dll",
                contentPackFor: "Nana.Provider"));
        File.WriteAllText(Path.Combine(target, "Hybrid.dll"), "assembly");
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "invalidManifest",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsMissingContentPackFor()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "InvalidPack");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, "manifest.json"),
            Manifest("Nana.InvalidPack", "1.0.0"));
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "invalidManifest",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsNestedManifest()
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            temporary.Path,
            "AmbiguousPack",
            "Nana.AmbiguousPack",
            "1.0.0",
            "Nana.Provider");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllText(
            Path.Combine(target, "nested", "manifest.json"),
            Manifest("Nana.Nested", "1.0.0", contentPackFor: "Nana.Provider"));
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "reviewReadyDirectoryInvalid",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsCSharpProjectAmbiguity()
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            temporary.Path,
            "AmbiguousPack",
            "Nana.AmbiguousPack",
            "1.0.0",
            "Nana.Provider");
        File.WriteAllText(Path.Combine(target, "Ambiguous.csproj"), "<Project />");
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "projectNotBuildable",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsInvalidProviderMinimumVersion()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "InvalidPack");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, "manifest.json"),
            """
            {
              "Name": "Invalid pack",
              "Author": "Nana",
              "UniqueID": "Nana.InvalidPack",
              "Version": "1.0.0",
              "Description": "Invalid provider version.",
              "ContentPackFor": {
                "UniqueID": "Nana.Provider",
                "MinimumVersion": "two"
              }
            }
            """);
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "reviewReadyManifestInvalid",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsNestedReparsePointWithoutReadingItsTargetWhenSupported()
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            temporary.Path,
            "TargetPack",
            "Nana.TargetPack",
            "1.0.0",
            "Nana.Provider");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(target, "linked"), outside);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        LiveLabPaths paths = ResolveLab(temporary.Path);
        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "reviewPreparationFailed",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsAReparsePointRootWhenSupported()
    {
        using TemporaryDirectory temporary = new();
        string actual = WriteContentPack(
            temporary.Path,
            "ActualPack",
            "Nana.TargetPack",
            "1.0.0",
            "Nana.Provider");
        string target = Path.Combine(temporary.Path, "TargetLink");
        try
        {
            Directory.CreateSymbolicLink(target, actual);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        LiveLabPaths paths = ResolveLab(temporary.Path);
        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "reviewPreparationFailed",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(actual, "content.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ExistingCSharpTargetStillUsesTheBuildPreflight()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "CodeTarget");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, "manifest.json"),
            Manifest("Nana.CodeTarget", "1.0.0", entryDll: "CodeTarget.dll"));
        File.WriteAllText(
            Path.Combine(target, "CodeTarget.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        LiveLabPaths paths = ResolveLab(temporary.Path);
        var doctorCalled = false;

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [],
            [],
            paths,
            () =>
            {
                doctorCalled = true;
                return new DoctorReport(1, DoctorReport.NotFound, []);
            });

        Assert.True(doctorCalled);
        Assert.Equal(
            "gameInstallationNotFound",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    [Fact]
    public void ContentPackTargetRejectsDuplicateStagingDirectory()
    {
        using TemporaryDirectory temporary = new();
        string target = WriteContentPack(
            Path.Combine(temporary.Path, "target-source"),
            "SameName",
            "Nana.TargetPack",
            "1.0.0",
            "Nana.Provider");
        string provider = WriteReadyCodeMod(
            Path.Combine(temporary.Path, "provider-source"),
            "SameName",
            "Nana.Provider",
            "1.0.0");
        LiveLabPaths paths = ResolveLab(temporary.Path);

        ProjectReviewPreparationResult result = ProjectModStager.PrepareReview(
            target,
            [provider],
            [],
            paths,
            DoctorMustNotRun);

        Assert.Equal(
            "reviewStagingNameCollision",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Null(result.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
    }

    private static string WriteContentPack(
        string root,
        string directoryName,
        string uniqueId,
        string version,
        string provider,
        string? providerMinimumVersion = null)
    {
        string path = Path.Combine(root, directoryName);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "manifest.json"),
            Manifest(
                uniqueId,
                version,
                contentPackFor: provider,
                providerMinimumVersion: providerMinimumVersion));
        File.WriteAllText(Path.Combine(path, "content.json"), "{}");
        return path;
    }

    private static LiveLabPaths ResolveLab(string root)
    {
        string lab = Path.Combine(root, "lab");
        Directory.CreateDirectory(lab);
        return LiveLabPaths.Resolve(lab);
    }

    private static string WriteReadyCodeMod(
        string root,
        string directoryName,
        string uniqueId,
        string version)
    {
        string path = Path.Combine(root, directoryName);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "manifest.json"),
            Manifest(uniqueId, version, entryDll: "Provider.dll"));
        File.WriteAllText(Path.Combine(path, "Provider.dll"), $"assembly:{uniqueId}");
        return path;
    }

    private static string Manifest(
        string uniqueId,
        string version,
        string? entryDll = null,
        string? contentPackFor = null,
        string? providerMinimumVersion = null)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["Name"] = "Review fixture",
            ["Author"] = "Nana",
            ["UniqueID"] = uniqueId,
            ["Version"] = version,
            ["Description"] = "Project review test fixture.",
        };
        if (entryDll is not null)
        {
            manifest["EntryDll"] = entryDll;
        }

        if (contentPackFor is not null)
        {
            manifest["ContentPackFor"] = new Dictionary<string, object?>
            {
                ["UniqueID"] = contentPackFor,
                ["MinimumVersion"] = providerMinimumVersion,
            };
        }

        return JsonSerializer.Serialize(manifest);
    }

    private static DoctorReport DoctorMustNotRun() =>
        throw new InvalidOperationException("Ready review artifacts must not require doctor.");

    private static DoctorReport ReadyDoctor() =>
        new(1, DoctorReport.Ready, [new DetectedInstallation("C:\\Game")]);

    private static string[] SnapshotTree(string root)
    {
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => SnapshotEntry(root, path))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
    }

    private static string SnapshotEntry(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        string fingerprint = Directory.Exists(path)
            ? "directory"
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return $"{relative}|{fingerprint}";
    }

    private static bool IsBelow(string root, string path)
    {
        string relative = Path.GetRelativePath(
            Path.GetFullPath(root),
            Path.GetFullPath(path));
        return relative.Length > 0
            && !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(contents);
    }
}

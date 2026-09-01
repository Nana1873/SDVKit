using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewStagerTests
{
    [Theory]
    [InlineData("duplicate", "reviewModIdentityCollision")]
    [InlineData("reserved", "reservedModIdentity")]
    public void ReviewSetRejectsDuplicateAndReservedUniqueIds(
        string caseName,
        string expectedCode)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            caseName == "reserved" ? "SDVKit.AlwaysOn" : "Nana.Target");
        IReadOnlyList<ProjectReviewPreparedArtifact> artifacts = caseName == "duplicate"
            ?
            [
                target,
                Artifact(
                    temporary.Path,
                    "Companion",
                    ProjectReviewArtifactRole.Companion,
                    "nana.target"),
            ]
            : [target];

        ProjectReviewProblem? problem = ProjectModStager.ValidateReviewSet(artifacts);

        Assert.Equal(expectedCode, Assert.IsType<ProjectReviewProblem>(problem).Code);
    }

    [Theory]
    [InlineData(false, "1.0.0", "reviewDependencyUnavailable")]
    [InlineData(true, "1.9.9", "reviewDependencyVersionMismatch")]
    public void ReviewSetRequiresExplicitDependencyAndMinimumVersion(
        bool includeCompanion,
        string companionVersion,
        string expectedCode)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            requiredDependencies:
            [
                new ProjectReviewDependency("Nana.Companion", "2.0.0"),
            ]);
        var artifacts = new List<ProjectReviewPreparedArtifact> { target };
        if (includeCompanion)
        {
            artifacts.Add(Artifact(
                temporary.Path,
                "Companion",
                ProjectReviewArtifactRole.Companion,
                "Nana.Companion",
                companionVersion));
        }

        ProjectReviewProblem? problem = ProjectModStager.ValidateReviewSet(artifacts);

        Assert.Equal(expectedCode, Assert.IsType<ProjectReviewProblem>(problem).Code);
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.2.1", "1.2.0", true)]
    [InlineData("1.1.9", "1.2.0", false)]
    [InlineData("1.0.0-alpha", "1.0.0-rc.1", false)]
    [InlineData("1.0.0-rc.1", "1.0.0-alpha", true)]
    [InlineData("1.0.0", "1.0.0-rc.1", true)]
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2", true)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", false)]
    [InlineData("1.0.0-1", "1.0.0-alpha", false)]
    [InlineData("1.0.0-alpha", "1.0.0-1", true)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", false)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", true)]
    [InlineData("1.0.0-rc.1+provided", "1.0.0-rc.1+minimum", true)]
    [InlineData("1.0.0-01", "1.0.0-1", false)]
    [InlineData("not-a-version", "1.0.0", false)]
    [InlineData("1.0.0", "not-a-version", false)]
    [InlineData("1.0.0-1", "1.0.0-01", false)]
    public void ReviewSetUsesSemVerPrecedenceForMinimumVersions(
        string providedVersion,
        string minimumVersion,
        bool expectedSatisfied)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            requiredDependencies:
            [
                new ProjectReviewDependency("Nana.Companion", minimumVersion),
            ]);
        ProjectReviewPreparedArtifact companion = Artifact(
            temporary.Path,
            "Companion",
            ProjectReviewArtifactRole.Companion,
            "Nana.Companion",
            providedVersion);

        ProjectReviewProblem? problem = ProjectModStager.ValidateReviewSet(
            [target, companion]);

        if (expectedSatisfied)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Equal(
                "reviewDependencyVersionMismatch",
                Assert.IsType<ProjectReviewProblem>(problem).Code);
        }
    }

    [Fact]
    public void ReadyTreeCopiesPlainNestedFiles()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "ready");
        string destination = Path.Combine(temporary.Path, "prepared");
        Directory.CreateDirectory(Path.Combine(source, "assets", "interiors"));
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{}");
        File.WriteAllText(
            Path.Combine(source, "assets", "interiors", "greenhouse.json"),
            "fixture");

        ProjectModStager.CopyReadyTree(source, destination);

        Assert.Equal(
            "fixture",
            File.ReadAllText(Path.Combine(
                destination,
                "assets",
                "interiors",
                "greenhouse.json")));
    }

    [Fact]
    public void ReadyTreeRejectsDirectoryLinksWithoutCopyingTheirTargetsWhenSupported()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "ready");
        string outside = Path.Combine(temporary.Path, "outside");
        string destination = Path.Combine(temporary.Path, "prepared");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
        }
        catch (Exception creationException) when (creationException is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProjectModStager.CopyReadyTree(source, destination));

        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(destination, "linked", "outside.txt")));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "outside.txt")));
    }

    [Fact]
    public void ReviewManifestParsesRequiredDependencyAndIgnoresMissingOptionalOne()
    {
        using TemporaryDirectory temporary = new();
        string manifestPath = temporary.WriteFile(
            "manifest.json",
            """
            {
              "Name": "Target",
              "Author": "Nana",
              "UniqueID": "Nana.Target",
              "Version": "1.0.0",
              "Description": "Target mod.",
              "EntryDll": "Target.dll",
              "Dependencies": [
                {
                  "UniqueID": "Nana.Required",
                  "IsRequired": true,
                  "MinimumVersion": "2.3.0"
                },
                {
                  "UniqueID": "Nana.Optional",
                  "IsRequired": false,
                  "MinimumVersion": "9.0.0"
                }
              ]
            }
            """);

        ProjectReviewManifest? manifest = ProjectModStager.ReadReviewManifest(
            manifestPath,
            allowVersionToken: false,
            out string? error);

        Assert.Null(error);
        ProjectReviewDependency dependency = Assert.Single(
            Assert.IsType<ProjectReviewManifest>(manifest).RequiredDependencies);
        Assert.Equal("Nana.Required", dependency.UniqueId);
        Assert.Equal("2.3.0", dependency.MinimumVersion);
    }

    [Fact]
    public void CustomContentPackForSelectedTargetIsAccepted()
    {
        using TemporaryDirectory temporary = new();
        string manifestPath = temporary.WriteFile(
            "custom-pack-manifest.json",
            """
            {
              "Name": "Custom pack",
              "Author": "Nana",
              "UniqueID": "Nana.Target.Pack",
              "Version": "1.0.0",
              "Description": "Native target content pack.",
              "ContentPackFor": {
                "UniqueID": "Nana.Target",
                "MinimumVersion": "1.2.0"
              }
            }
            """);
        ProjectReviewManifest? parsed = ProjectModStager.ReadReviewManifest(
            manifestPath,
            allowVersionToken: false,
            out string? error);
        ProjectReviewManifest packManifest = Assert.IsType<ProjectReviewManifest>(parsed);
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            "1.2.0");
        ProjectReviewPreparedArtifact pack = Artifact(
            temporary.Path,
            "TargetPack",
            ProjectReviewArtifactRole.ContentPack,
            packManifest.UniqueId,
            packManifest.Version,
            contentPackFor: packManifest.ContentPackFor,
            contentPackForMinimumVersion: packManifest.ContentPackForMinimumVersion);

        ProjectReviewProblem? problem = ProjectModStager.ValidateReviewSet([target, pack]);

        Assert.Null(error);
        Assert.Equal(ProjectInspectionReport.ContentPack, packManifest.Kind);
        Assert.Equal("Nana.Target", packManifest.ContentPackFor);
        Assert.Equal("1.2.0", packManifest.ContentPackForMinimumVersion);
        Assert.Null(problem);
    }

    [Fact]
    public void BatchStagingAndCleanupPreserveAlwaysOnAndPersistentSaves()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string alwaysOnSentinel = WriteSentinel(
            paths.AlwaysOnModPath,
            "always-on.txt",
            "always-on");
        string saveSentinel = WriteSentinel(
            Path.Combine(paths.SavesPath, "SICQA_Review"),
            "SaveGameInfo",
            "persistent-save");
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            "1.2.0");
        ProjectReviewPreparedArtifact companion = Artifact(
            temporary.Path,
            "Harness",
            ProjectReviewArtifactRole.Companion,
            "Nana.Harness");
        ProjectReviewPreparedArtifact contentPack = Artifact(
            temporary.Path,
            "SmokePack",
            ProjectReviewArtifactRole.ContentPack,
            "Nana.Target.SmokePack",
            contentPackFor: "Nana.Target",
            contentPackForMinimumVersion: "1.0.0");

        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target, companion, contentPack],
            paths);

        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(result.Staging);
        Assert.Null(result.Problem);
        Assert.Equal(LiveLabState.SingleTopology, staging.Topology);
        Assert.Equal(3, staging.Artifacts.Count);
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.All(staging.Artifacts, artifact =>
        {
            ProjectReviewRoleStagingPath rolePath = Assert.Single(
                artifact.RoleStagingPaths);
            Assert.Equal(LiveLabState.SingleTopology, rolePath.Role);
            Assert.Equal(rolePath.StagingPath, artifact.StagingPath);
            Assert.True(Directory.Exists(artifact.StagingPath));
            Assert.Equal(
                artifact.BuildIdentity,
                ModBuildIdentity.ComputeFileSet(artifact.StagingPath));
        });

        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);

        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.Null(cleanup.Problem);
        Assert.All(
            staging.Artifacts,
            artifact => Assert.False(Directory.Exists(artifact.StagingPath)));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.Equal("always-on", File.ReadAllText(alwaysOnSentinel));
        Assert.Equal("persistent-save", File.ReadAllText(saveSentinel));
    }

    [Fact]
    public void NetworkTwoStagesTheSameReviewSetForHostAndFarmhandAndCleansBoth()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        hostPaths.EnsureDirectories();
        farmhandPaths.EnsureDirectories();
        string hostAlwaysOn = WriteSentinel(
            hostPaths.AlwaysOnModPath,
            "always-on.txt",
            "host-always-on");
        string farmhandAlwaysOn = WriteSentinel(
            farmhandPaths.AlwaysOnModPath,
            "always-on.txt",
            "farmhand-always-on");
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target",
            "1.2.0");
        ProjectReviewPreparedArtifact companion = Artifact(
            temporary.Path,
            "Harness",
            ProjectReviewArtifactRole.Companion,
            "Nana.Harness");
        ProjectReviewPreparedArtifact contentPack = Artifact(
            temporary.Path,
            "SmokePack",
            ProjectReviewArtifactRole.ContentPack,
            "Nana.Target.SmokePack",
            contentPackFor: "Nana.Target",
            contentPackForMinimumVersion: "1.0.0");

        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target, companion, contentPack],
            NetworkTwoContract.Topology,
            paths);

        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(result.Staging);
        Assert.Null(result.Problem);
        Assert.Equal(NetworkTwoContract.Topology, staging.Topology);
        Assert.Equal(
            staging.Target.BuildIdentity,
            staging.TargetLaunchState.BuildIdentity);
        Assert.Equal(
            Path.Combine(
                temporary.Path,
                ".sdvkit",
                "lab",
                NetworkTwoContract.Topology,
                "project-review-staging.json"),
            staging.OwnershipPath);
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.All(staging.Artifacts, artifact =>
        {
            Assert.Equal(
                new[]
                {
                    NetworkTwoContract.HostRole,
                    NetworkTwoContract.FarmhandRole,
                },
                artifact.RoleStagingPaths.Select(path => path.Role));
            string hostStagingPath = artifact.StagingPathFor(
                NetworkTwoContract.HostRole);
            string farmhandStagingPath = artifact.StagingPathFor(
                NetworkTwoContract.FarmhandRole);
            Assert.Equal(hostPaths.ModsPath, Path.GetDirectoryName(hostStagingPath));
            Assert.Equal(farmhandPaths.ModsPath, Path.GetDirectoryName(farmhandStagingPath));
            Assert.NotEqual(hostStagingPath, farmhandStagingPath);
            Assert.Equal(
                artifact.BuildIdentity,
                ModBuildIdentity.ComputeFileSet(hostStagingPath));
            Assert.Equal(
                artifact.BuildIdentity,
                ModBuildIdentity.ComputeFileSet(farmhandStagingPath));
        });

        ProjectReviewStagingResult read = ProjectModStager.ReadReview(
            paths,
            NetworkTwoContract.Topology);
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(
            paths,
            NetworkTwoContract.Topology);

        Assert.NotNull(read.Staging);
        Assert.Null(read.Problem);
        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.Null(cleanup.Problem);
        Assert.All(staging.Artifacts, artifact =>
        {
            Assert.False(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.HostRole)));
            Assert.False(Directory.Exists(artifact.StagingPathFor(
                NetworkTwoContract.FarmhandRole)));
        });
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.Equal("host-always-on", File.ReadAllText(hostAlwaysOn));
        Assert.Equal("farmhand-always-on", File.ReadAllText(farmhandAlwaysOn));
    }

    [Fact]
    public void NetworkTwoCleanupRetriesAfterOneOwnedRolePathWasAlreadyRemoved()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(
            ProjectModStager.StageReview(
                [target],
                NetworkTwoContract.Topology,
                paths).Staging);
        string hostStagingPath = staging.Target.StagingPathFor(
            NetworkTwoContract.HostRole);
        string farmhandStagingPath = staging.Target.StagingPathFor(
            NetworkTwoContract.FarmhandRole);
        Directory.Delete(hostStagingPath, recursive: true);

        ProjectReviewStagingResult read = ProjectModStager.ReadReview(
            paths,
            NetworkTwoContract.Topology);
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(
            paths,
            NetworkTwoContract.Topology);

        Assert.Null(read.Staging);
        Assert.Equal(
            "reviewStagingOwnershipInvalid",
            Assert.IsType<ProjectReviewProblem>(read.Problem).Code);
        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.Null(cleanup.Problem);
        Assert.False(Directory.Exists(farmhandStagingPath));
        Assert.False(File.Exists(staging.OwnershipPath));
    }

    [Fact]
    public void NetworkTwoSmokeOwnershipBlocksReviewWithoutStagingEitherRole()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        hostPaths.EnsureDirectories();
        farmhandPaths.EnsureDirectories();
        WriteSentinel(
            Path.Combine(
                temporary.Path,
                ".sdvkit",
                "lab",
                NetworkTwoContract.Topology),
            "project-smoke-staging.json",
            "retained-smoke-ownership");
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");

        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target],
            NetworkTwoContract.Topology,
            paths);

        Assert.Null(result.Staging);
        Assert.Equal(
            "smokeStagingOwnershipPresent",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.False(Directory.Exists(Path.Combine(hostPaths.ModsPath, "Target")));
        Assert.False(Directory.Exists(Path.Combine(farmhandPaths.ModsPath, "Target")));
    }

    [Fact]
    public void NetworkTwoFarmhandForeignModBlocksReviewBeforeHostMutation()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            paths,
            NetworkTwoContract.FarmhandRole);
        hostPaths.EnsureDirectories();
        farmhandPaths.EnsureDirectories();
        string foreignSentinel = WriteSentinel(
            Path.Combine(farmhandPaths.ModsPath, "ForeignMod"),
            "sentinel.txt",
            "foreign");
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");

        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target],
            NetworkTwoContract.Topology,
            paths);

        Assert.Null(result.Staging);
        Assert.Equal(
            "foreignLabModCollision",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.False(Directory.Exists(Path.Combine(hostPaths.ModsPath, "Target")));
        Assert.False(Directory.Exists(Path.Combine(farmhandPaths.ModsPath, "Target")));
        Assert.Equal("foreign", File.ReadAllText(foreignSentinel));
    }

    [Fact]
    public void ForeignModCollisionIsBlockedWithoutMutation()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string foreignSentinel = WriteSentinel(
            Path.Combine(paths.ModsPath, "ForeignMod"),
            "sentinel.txt",
            "foreign");
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");

        ProjectReviewStagingResult result = ProjectModStager.StageReview([target], paths);

        Assert.Null(result.Staging);
        Assert.Equal(
            "foreignLabModCollision",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.Equal("foreign", File.ReadAllText(foreignSentinel));
        Assert.False(Directory.Exists(Path.Combine(paths.ModsPath, "Target")));
    }

    [Fact]
    public void IncompleteRollbackLeavesMarkerlessPartialStagingDiscoverable()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        string stagingPath = Path.Combine(paths.ModsPath, "Target");

        ProjectReviewStagingResult result = ProjectModStager.StageReview(
            [target],
            paths,
            copyTree: (_, destination) =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "partial.dll"), "partial");
                throw new IOException("Injected staging copy failure.");
            },
            deleteTree: _ => false);

        Assert.Null(result.Staging);
        Assert.Equal(
            "reviewStagingRollbackIncomplete",
            Assert.IsType<ProjectReviewProblem>(result.Problem).Code);
        Assert.True(Directory.Exists(stagingPath));
        Assert.False(File.Exists(Path.Combine(
            paths.SingleRoot,
            "project-review-staging.json")));

        ProjectReviewStagingResult retained = ProjectModStager.ReadReview(paths);

        Assert.Null(retained.Staging);
        Assert.Equal(
            "reviewStagingOwnershipMissing",
            Assert.IsType<ProjectReviewProblem>(retained.Problem).Code);
        Assert.Equal(
            "partial",
            File.ReadAllText(Path.Combine(stagingPath, "partial.dll")));
    }

    [Fact]
    public void DriftedOwnedStagingBlocksReadAndCleanup()
    {
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        paths.EnsureDirectories();
        string alwaysOnSentinel = WriteSentinel(
            paths.AlwaysOnModPath,
            "always-on.txt",
            "always-on");
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(
            ProjectModStager.StageReview([target], paths).Staging);
        string stagedDll = Path.Combine(staging.Target.StagingPath, "Target.dll");
        File.AppendAllText(stagedDll, "drift");

        ProjectReviewStagingResult read = ProjectModStager.ReadReview(paths);
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);

        Assert.Null(read.Staging);
        Assert.Equal(
            "reviewStagingOwnershipDrifted",
            Assert.IsType<ProjectReviewProblem>(read.Problem).Code);
        Assert.False(cleanup.Removed);
        Assert.Equal(
            "reviewStagingOwnershipDrifted",
            Assert.IsType<ProjectReviewProblem>(cleanup.Problem).Code);
        Assert.True(Directory.Exists(staging.Target.StagingPath));
        Assert.EndsWith("drift", File.ReadAllText(stagedDll), StringComparison.Ordinal);
        Assert.True(File.Exists(staging.OwnershipPath));
        Assert.Equal("always-on", File.ReadAllText(alwaysOnSentinel));
    }

    [Fact]
    public void RuntimeRootConfigIsAcceptedForCodeModTargetAndCompanion()
    {
        const string secret = "issue-40-secret-must-not-be-reported";
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewPreparedArtifact companion = Artifact(
            temporary.Path,
            "Companion",
            ProjectReviewArtifactRole.Companion,
            "Nana.Companion");
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(
            ProjectModStager.StageReview([target, companion], paths).Staging);
        foreach (ProjectReviewOwnedArtifact artifact in staging.Artifacts)
        {
            File.WriteAllText(Path.Combine(artifact.StagingPath, "config.json"), secret);
        }

        ProjectReviewStagingResult read = ProjectModStager.ReadReview(paths);
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);

        Assert.NotNull(read.Staging);
        Assert.Null(read.Problem);
        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.Null(cleanup.Problem);
        Assert.DoesNotContain(
            secret,
            JsonSerializer.Serialize(read),
            StringComparison.Ordinal);
        Assert.All(
            staging.Artifacts,
            artifact => Assert.False(Directory.Exists(artifact.StagingPath)));
        Assert.False(File.Exists(staging.OwnershipPath));
    }

    [Fact]
    public void RuntimeRootConfigRemainsDriftForAContentPack()
    {
        const string secret = "issue-40-content-pack-secret-must-not-be-reported";
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewPreparedArtifact contentPack = Artifact(
            temporary.Path,
            "Pack",
            ProjectReviewArtifactRole.ContentPack,
            "Nana.Target.Pack",
            contentPackFor: "Nana.Target");
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(
            ProjectModStager.StageReview([target, contentPack], paths).Staging);
        ProjectReviewOwnedArtifact stagedPack = staging.Artifacts.Single(artifact =>
            string.Equals(
                artifact.Role,
                ProjectReviewArtifactRole.ContentPack,
                StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(stagedPack.StagingPath, "config.json"), secret);

        ProjectReviewStagingResult read = ProjectModStager.ReadReview(paths);
        ProjectReviewCleanupResult cleanup = ProjectModStager.RemoveReview(paths);

        Assert.Null(read.Staging);
        Assert.Equal(
            "reviewStagingOwnershipDrifted",
            Assert.IsType<ProjectReviewProblem>(read.Problem).Code);
        Assert.False(cleanup.Removed);
        ProjectReviewProblem cleanupProblem = Assert.IsType<ProjectReviewProblem>(
            cleanup.Problem);
        Assert.Equal("reviewStagingOwnershipDrifted", cleanupProblem.Code);
        Assert.DoesNotContain(secret, cleanupProblem.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(stagedPack.StagingPath));
        Assert.True(File.Exists(staging.OwnershipPath));
    }

    [Fact]
    public void RuntimeRootConfigCleanupFailureRetainsReviewOwnership()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string secret = "issue-40-cleanup-secret-must-not-be-reported";
        using TemporaryDirectory temporary = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temporary.Path);
        ProjectReviewPreparedArtifact target = Artifact(
            temporary.Path,
            "Target",
            ProjectReviewArtifactRole.Target,
            "Nana.Target");
        ProjectReviewStaging staging = Assert.IsType<ProjectReviewStaging>(
            ProjectModStager.StageReview([target], paths).Staging);
        File.WriteAllText(
            Path.Combine(staging.Target.StagingPath, "config.json"),
            secret);
        string stagedDll = Path.Combine(staging.Target.StagingPath, "Target.dll");

        ProjectReviewCleanupResult blocked;
        using (new FileStream(stagedDll, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            blocked = ProjectModStager.RemoveReview(paths);
        }

        Assert.False(blocked.Removed);
        ProjectReviewProblem problem = Assert.IsType<ProjectReviewProblem>(blocked.Problem);
        Assert.Equal("reviewStagingCleanupFailed", problem.Code);
        Assert.DoesNotContain(secret, problem.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(staging.Target.StagingPath));
        Assert.True(File.Exists(staging.OwnershipPath));
    }

    internal static ProjectReviewPreparedArtifact Artifact(
        string root,
        string topLevelDirectory,
        string role,
        string uniqueId,
        string version = "1.0.0",
        IReadOnlyList<ProjectReviewDependency>? requiredDependencies = null,
        string? contentPackFor = null,
        string? contentPackForMinimumVersion = null,
        string? kind = null)
    {
        string preparedPath = Path.Combine(
            root,
            "review-test-prepared",
            $"{topLevelDirectory}-{Guid.NewGuid():N}",
            topLevelDirectory);
        Directory.CreateDirectory(preparedPath);
        bool contentPack = string.Equals(
            kind,
            ProjectInspectionReport.ContentPack,
            StringComparison.Ordinal)
            || (kind is null
                && string.Equals(
                    role,
                    ProjectReviewArtifactRole.ContentPack,
                    StringComparison.Ordinal));
        string? entryDll = contentPack ? null : $"{topLevelDirectory}.dll";
        var manifest = new ProjectReviewManifest(
            contentPack
                ? ProjectInspectionReport.ContentPack
                : ProjectInspectionReport.SmapiMod,
            topLevelDirectory,
            uniqueId,
            version,
            entryDll,
            contentPackFor,
            contentPackForMinimumVersion,
            requiredDependencies ?? []);
        File.WriteAllText(
            Path.Combine(preparedPath, "manifest.json"),
            ManifestJson(manifest));
        if (entryDll is not null)
        {
            File.WriteAllText(Path.Combine(preparedPath, entryDll), $"assembly:{uniqueId}");
        }
        else
        {
            File.WriteAllText(Path.Combine(preparedPath, "interiors.json"), "{\"Interiors\":[]}");
        }

        return new ProjectReviewPreparedArtifact(
            role,
            preparedPath,
            preparedPath,
            topLevelDirectory,
            manifest,
            ModBuildIdentity.ComputeFileSet(preparedPath),
            null,
            null);
    }

    private static string ManifestJson(ProjectReviewManifest manifest)
    {
        var root = new Dictionary<string, object?>
        {
            ["Name"] = manifest.Name,
            ["Author"] = "Nana",
            ["UniqueID"] = manifest.UniqueId,
            ["Version"] = manifest.Version,
            ["Description"] = "Project review test fixture.",
        };
        if (manifest.EntryDll is not null)
        {
            root["EntryDll"] = manifest.EntryDll;
            root["Dependencies"] = manifest.RequiredDependencies.Select(dependency =>
                new Dictionary<string, object?>
                {
                    ["UniqueID"] = dependency.UniqueId,
                    ["IsRequired"] = true,
                    ["MinimumVersion"] = dependency.MinimumVersion,
                }).ToArray();
        }
        else
        {
            root["ContentPackFor"] = new Dictionary<string, object?>
            {
                ["UniqueID"] = manifest.ContentPackFor,
                ["MinimumVersion"] = manifest.ContentPackForMinimumVersion,
            };
        }

        return JsonSerializer.Serialize(root);
    }

    private static string WriteSentinel(
        string directory,
        string fileName,
        string contents)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }
}

using System.IO.Compression;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectModStagerTests
{
    private const string ModDirectoryName = "ExampleMod";
    private const string ModEntryDll = "ExampleMod.dll";
    private const string ModUniqueId = "Nana.ExampleMod";

    [Fact]
    public void SingleStageUsesOnlyTheValidatedPackageAndCleanupPreservesAlwaysOn()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: "single.zip",
            assemblyContents: "packaged assembly",
            additionalFiles: new Dictionary<string, string>
            {
                ["assets/data.json"] = "packaged asset",
                ["lib/Bundled.dll"] = "bundled dependency",
            });
        project.WriteFile("bin/Release/net6.0/ExampleMod.dll", "different bin assembly");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        WriteAlwaysOn(paths, "always-on sentinel");

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        ProjectModStaging staging = AssertSuccessful(result);
        string stagedPath = Assert.Single(staging.StagingPaths);
        Assert.Equal(
            ExpectedModFiles(fixture.Package),
            FilesBelow(stagedPath));
        Assert.Equal(
            "packaged assembly",
            File.ReadAllText(Path.Combine(stagedPath, ModEntryDll)));
        Assert.DoesNotContain(
            "bin/Release/net6.0/ExampleMod.dll",
            FilesBelow(stagedPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            ModBuildIdentity.ComputeFile(fixture.ArchivePath),
            staging.Artifact.PackageHash);
        Assert.Equal(
            ModBuildIdentity.ComputeFileSet(stagedPath),
            staging.Artifact.BuildIdentity);
        Assert.True(File.Exists(staging.OwnershipPath));

        ProjectModCleanupResult cleanup = ProjectModStager.Remove(staging);

        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.Null(cleanup.Problem);
        Assert.False(Directory.Exists(stagedPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.Equal(
            "always-on sentinel",
            File.ReadAllText(Path.Combine(paths.AlwaysOnModPath, "sentinel.txt")));
    }

    [Fact]
    public void RuntimeCreatedRootConfigJsonAllowsExactOwnedSmokeCleanup()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(project, archiveName: "runtime-config.zip");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        WriteAlwaysOn(paths, "always-on sentinel");
        ProjectModStaging staging = AssertSuccessful(ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths));
        string stagingPath = Assert.Single(staging.StagingPaths);
        const string secret = "SharedSecret=runtime-only-value";
        File.WriteAllText(
            Path.Combine(stagingPath, "config.json"),
            $"{{\"SharedSecret\":\"{secret}\"}}");

        string ownershipMarker = File.ReadAllText(staging.OwnershipPath);
        ProjectModCleanupResult cleanup = ProjectModStager.Remove(staging);

        Assert.DoesNotContain(secret, ownershipMarker, StringComparison.Ordinal);
        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.Null(cleanup.Problem);
        Assert.False(Directory.Exists(stagingPath));
        Assert.False(File.Exists(staging.OwnershipPath));
        Assert.Equal(
            "always-on sentinel",
            File.ReadAllText(Path.Combine(paths.AlwaysOnModPath, "sentinel.txt")));
    }

    [Fact]
    public void NetworkStageCopiesOneIdenticalBuildToHostAndFarmhand()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: "network.zip",
            additionalFiles: new Dictionary<string, string>
            {
                ["assets/data.json"] = "asset",
                ["lib/Bundled.dll"] = "bundled dependency",
            });
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(project.Path);
        LiveLabPaths hostPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.HostRole);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        WriteAlwaysOn(hostPaths, "host always-on");
        WriteAlwaysOn(farmhandPaths, "farmhand always-on");

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            NetworkTwoContract.Topology,
            singlePaths);

        ProjectModStaging staging = AssertSuccessful(result);
        Assert.Equal(2, staging.StagingPaths.Count);
        Assert.Equal(
            Path.Combine(hostPaths.ModsPath, ModDirectoryName),
            staging.StagingPaths[0]);
        Assert.Equal(
            Path.Combine(farmhandPaths.ModsPath, ModDirectoryName),
            staging.StagingPaths[1]);
        Assert.Equal(
            FilesBelow(staging.StagingPaths[0]),
            FilesBelow(staging.StagingPaths[1]));
        Assert.All(
            staging.StagingPaths,
            path => Assert.Equal(
                staging.Artifact.BuildIdentity,
                ModBuildIdentity.ComputeFileSet(path)));

        ProjectModCleanupResult cleanup = ProjectModStager.Remove(staging);

        Assert.True(cleanup.Removed, cleanup.Problem?.Message);
        Assert.All(staging.StagingPaths, path => Assert.False(Directory.Exists(path)));
        Assert.True(File.Exists(Path.Combine(hostPaths.AlwaysOnModPath, "sentinel.txt")));
        Assert.True(File.Exists(Path.Combine(farmhandPaths.AlwaysOnModPath, "sentinel.txt")));
    }

    [Fact]
    public void UnownedCollisionIsReportedAndLeftUntouched()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(project, archiveName: "collision.zip");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        paths.EnsureDirectories();
        string collisionPath = Path.Combine(paths.ModsPath, ModDirectoryName);
        Directory.CreateDirectory(collisionPath);
        File.WriteAllText(Path.Combine(collisionPath, "sentinel.txt"), "foreign");

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        Assert.Null(result.Staging);
        Assert.Equal("modStagingCollision", Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        Assert.Equal(
            "foreign",
            File.ReadAllText(Path.Combine(collisionPath, "sentinel.txt")));
        Assert.False(File.Exists(OwnershipPath(project.Path, LiveLabState.SingleTopology)));
    }

    [Fact]
    public void PartialCopyFailureRollsBackTheExactDestination()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(project, archiveName: "copy-failure.zip");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths,
            (_, destination) =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "partial.txt"), "partial");
                throw new IOException("Injected copy failure.");
            });

        Assert.Null(result.Staging);
        Assert.Equal(
            "projectStagingFailed",
            Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        Assert.False(Directory.Exists(Path.Combine(paths.ModsPath, ModDirectoryName)));
        Assert.False(File.Exists(OwnershipPath(project.Path, LiveLabState.SingleTopology)));
    }

    [Fact]
    public void PartialCopyRollbackFailureIsReportedAsRetainedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(project, archiveName: "rollback-failure.zip");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        FileStream? lockedFile = null;
        ProjectModStagingResult result;
        try
        {
            result = ProjectModStager.Stage(
                fixture.Package,
                fixture.Target,
                LiveLabState.SingleTopology,
                paths,
                (_, destination) =>
                {
                    Directory.CreateDirectory(destination);
                    lockedFile = new FileStream(
                        Path.Combine(destination, "locked.txt"),
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    throw new IOException("Injected copy failure with a locked partial file.");
                });
        }
        finally
        {
            lockedFile?.Dispose();
        }

        Assert.Null(result.Staging);
        Assert.Equal(
            "projectStagingRollbackIncomplete",
            Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        string retainedPath = Path.Combine(paths.ModsPath, ModDirectoryName);
        Assert.True(Directory.Exists(retainedPath));
        Assert.False(File.Exists(OwnershipPath(project.Path, LiveLabState.SingleTopology)));
        Directory.Delete(retainedPath, recursive: true);
    }

    [Fact]
    public void PreparedCleanupFailureRollsBackTargetAndBlocksLaunchOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(project, archiveName: "prepared-cleanup.zip");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        FileStream? lockedPreparedFile = null;
        ProjectModStagingResult result;
        try
        {
            result = ProjectModStager.Stage(
                fixture.Package,
                fixture.Target,
                LiveLabState.SingleTopology,
                paths,
                (source, destination) =>
                {
                    CopyTree(source, destination);
                    lockedPreparedFile = new FileStream(
                        Path.Combine(source, "manifest.json"),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                });
        }
        finally
        {
            lockedPreparedFile?.Dispose();
        }

        Assert.Null(result.Staging);
        Assert.Equal(
            "preparedStagingCleanupIncomplete",
            Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        Assert.False(Directory.Exists(Path.Combine(paths.ModsPath, ModDirectoryName)));
        Assert.False(File.Exists(OwnershipPath(project.Path, LiveLabState.SingleTopology)));
        string prepared = Assert.Single(Directory.GetDirectories(
            paths.ModsPath,
            ".sdvkit-project-smoke-prepared-*"));
        Directory.Delete(prepared, recursive: true);
    }

    [Fact]
    public void EarlyOutcomeReportsRetainedPreparedPackageWhenCleanupFailsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: "early-prepared-cleanup.zip",
            dependenciesJson: "[{ \"UniqueID\": \"Pathoschild.ContentPatcher\" }]");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        FileStream? lockedPreparedFile = null;
        ProjectModStagingResult result;
        try
        {
            result = ProjectModStager.Stage(
                fixture.Package,
                fixture.Target,
                LiveLabState.SingleTopology,
                paths,
                afterPrepare: preparedModPath =>
                {
                    lockedPreparedFile = new FileStream(
                        Path.Combine(preparedModPath, "manifest.json"),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                });
        }
        finally
        {
            lockedPreparedFile?.Dispose();
        }

        Assert.Null(result.Staging);
        Assert.Equal(
            "preparedStagingCleanupIncomplete",
            Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        string prepared = Assert.Single(Directory.GetDirectories(
            paths.ModsPath,
            ".sdvkit-project-smoke-prepared-*"));
        Directory.Delete(prepared, recursive: true);
    }

    [Fact]
    public void ReservedAlwaysOnDirectoryCollisionIsBlockedAndPreserved()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: "reserved-directory.zip",
            modDirectoryName: "SDVKit.AlwaysOn");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        WriteAlwaysOn(paths, "always-on sentinel");

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        Assert.Null(result.Staging);
        Assert.Equal(
            "reservedModStagingPath",
            Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        Assert.Equal(
            "always-on sentinel",
            File.ReadAllText(Path.Combine(paths.AlwaysOnModPath, "sentinel.txt")));
    }

    [Fact]
    public void PreparedModGroupReparsePointIsRejectedWhenLinksAreSupported()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory outside = new();
        PackageFixture fixture = CreatePackage(project, archiveName: "reparse.zip");
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        paths.EnsureDirectories();
        Directory.Delete(paths.ModsPath);
        try
        {
            Directory.CreateSymbolicLink(paths.ModsPath, outside.Path);
        }
        catch (Exception exception) when (exception is IOException
            or PlatformNotSupportedException
            or UnauthorizedAccessException)
        {
            return;
        }

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        Assert.Null(result.Staging);
        ProjectSmokeProblem problem = Assert.IsType<ProjectSmokeProblem>(result.Problem);
        Assert.Equal("projectStagingFailed", problem.Code);
        Assert.Contains("reparse point", problem.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public void ExactOwnedStaleStageBlocksTheNextRunWithoutMutation()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        PackageFixture firstPackage = CreatePackage(
            project,
            archiveName: "first.zip",
            packageVersion: "1.0.0",
            assemblyContents: "first assembly",
            additionalFiles: new Dictionary<string, string>
            {
                ["old-only.txt"] = "old",
            });
        ProjectModStaging first = AssertSuccessful(ProjectModStager.Stage(
            firstPackage.Package,
            firstPackage.Target,
            LiveLabState.SingleTopology,
            paths));
        string stagingPath = Assert.Single(first.StagingPaths);
        const string retainedSecret = "SharedSecret=retained-runtime-value";
        string retainedConfigPath = Path.Combine(stagingPath, "config.json");
        File.WriteAllText(retainedConfigPath, retainedSecret);
        PackageFixture secondPackage = CreatePackage(
            project,
            archiveName: "second.zip",
            packageVersion: "2.0.0",
            assemblyContents: "second assembly",
            additionalFiles: new Dictionary<string, string>
            {
                ["new-only.txt"] = "new",
            });

        ProjectModStagingResult replacementResult = ProjectModStager.Stage(
            secondPackage.Package,
            secondPackage.Target,
            LiveLabState.SingleTopology,
            paths);

        Assert.Null(replacementResult.Staging);
        Assert.Equal(
            "stagingOwnershipPresent",
            Assert.IsType<ProjectSmokeProblem>(replacementResult.Problem).Code);
        Assert.Equal("old", File.ReadAllText(Path.Combine(stagingPath, "old-only.txt")));
        Assert.False(File.Exists(Path.Combine(stagingPath, "new-only.txt")));
        Assert.Equal("first assembly", File.ReadAllText(Path.Combine(stagingPath, ModEntryDll)));
        Assert.Equal(retainedSecret, File.ReadAllText(retainedConfigPath));
        Assert.DoesNotContain(
            retainedSecret,
            Assert.IsType<ProjectSmokeProblem>(replacementResult.Problem).Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            retainedSecret,
            File.ReadAllText(first.OwnershipPath),
            StringComparison.Ordinal);
        Assert.True(File.Exists(first.OwnershipPath));
        Assert.True(ProjectModStager.Remove(first).Removed);
    }

    [Fact]
    public void DriftedOwnedContentBlocksReplacementAndRemoval()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        PackageFixture firstPackage = CreatePackage(project, archiveName: "owned.zip");
        ProjectModStaging owned = AssertSuccessful(ProjectModStager.Stage(
            firstPackage.Package,
            firstPackage.Target,
            LiveLabState.SingleTopology,
            paths));
        string stagingPath = Assert.Single(owned.StagingPaths);
        const string secret = "SharedSecret=must-not-be-reported";
        File.WriteAllText(Path.Combine(stagingPath, "config.json"), secret);
        File.AppendAllText(Path.Combine(stagingPath, ModEntryDll), "drift");
        PackageFixture replacementPackage = CreatePackage(
            project,
            archiveName: "replacement.zip",
            packageVersion: "2.0.0");

        ProjectModStagingResult replacement = ProjectModStager.Stage(
            replacementPackage.Package,
            replacementPackage.Target,
            LiveLabState.SingleTopology,
            paths);
        ProjectModCleanupResult cleanup = ProjectModStager.Remove(owned);

        Assert.Null(replacement.Staging);
        Assert.Equal(
            "stagingOwnershipDrifted",
            Assert.IsType<ProjectSmokeProblem>(replacement.Problem).Code);
        Assert.False(cleanup.Removed);
        Assert.Equal(
            "stagingOwnershipDrifted",
            Assert.IsType<ProjectSmokeProblem>(cleanup.Problem).Code);
        Assert.DoesNotContain(
            secret,
            Assert.IsType<ProjectSmokeProblem>(replacement.Problem).Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            Assert.IsType<ProjectSmokeProblem>(cleanup.Problem).Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            File.ReadAllText(owned.OwnershipPath),
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(stagingPath));
        Assert.EndsWith(
            "drift",
            File.ReadAllText(Path.Combine(stagingPath, ModEntryDll)),
            StringComparison.Ordinal);
        Assert.True(File.Exists(owned.OwnershipPath));
    }

    [Fact]
    public void DriftedOwnershipMarkerBlocksReplacementAndRemoval()
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        PackageFixture firstPackage = CreatePackage(project, archiveName: "owned-marker.zip");
        ProjectModStaging owned = AssertSuccessful(ProjectModStager.Stage(
            firstPackage.Package,
            firstPackage.Target,
            LiveLabState.SingleTopology,
            paths));
        string changedIdentity = $"sha256:{new string('0', 64)}";
        string marker = File.ReadAllText(owned.OwnershipPath).Replace(
            owned.Artifact.BuildIdentity,
            changedIdentity,
            StringComparison.Ordinal);
        File.WriteAllText(owned.OwnershipPath, marker);
        PackageFixture replacementPackage = CreatePackage(
            project,
            archiveName: "marker-replacement.zip",
            packageVersion: "2.0.0");

        ProjectModStagingResult replacement = ProjectModStager.Stage(
            replacementPackage.Package,
            replacementPackage.Target,
            LiveLabState.SingleTopology,
            paths);
        ProjectModCleanupResult cleanup = ProjectModStager.Remove(owned);

        Assert.Null(replacement.Staging);
        Assert.Equal(
            "stagingOwnershipDrifted",
            Assert.IsType<ProjectSmokeProblem>(replacement.Problem).Code);
        Assert.False(cleanup.Removed);
        Assert.Equal(
            "stagingOwnershipMismatch",
            Assert.IsType<ProjectSmokeProblem>(cleanup.Problem).Code);
        Assert.True(Directory.Exists(Assert.Single(owned.StagingPaths)));
        Assert.True(File.Exists(owned.OwnershipPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrNullOwnershipPathsBlockReplacementWithoutMutation(
        bool includeNullPaths)
    {
        using TemporaryDirectory project = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);
        PackageFixture firstPackage = CreatePackage(project, archiveName: "owned-paths.zip");
        ProjectModStaging owned = AssertSuccessful(ProjectModStager.Stage(
            firstPackage.Package,
            firstPackage.Target,
            LiveLabState.SingleTopology,
            paths));
        var marker = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["topology"] = LiveLabState.SingleTopology,
            ["uniqueId"] = owned.Artifact.Manifest.UniqueId,
            ["version"] = owned.Artifact.Manifest.Version,
            ["packageHash"] = owned.Artifact.PackageHash,
            ["buildIdentity"] = owned.Artifact.BuildIdentity,
        };
        if (includeNullPaths)
        {
            marker["stagingPaths"] = null;
        }

        File.WriteAllText(owned.OwnershipPath, JsonSerializer.Serialize(marker));
        PackageFixture replacementPackage = CreatePackage(
            project,
            archiveName: "paths-replacement.zip",
            packageVersion: "2.0.0");

        ProjectModStagingResult replacement = ProjectModStager.Stage(
            replacementPackage.Package,
            replacementPackage.Target,
            LiveLabState.SingleTopology,
            paths);

        Assert.Null(replacement.Staging);
        Assert.Equal(
            "stagingOwnershipInvalid",
            Assert.IsType<ProjectSmokeProblem>(replacement.Problem).Code);
        Assert.True(Directory.Exists(Assert.Single(owned.StagingPaths)));
        Assert.True(File.Exists(owned.OwnershipPath));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "runtimeDependencyUnavailable")]
    public void RequiredDependencyIsRejectedWhileOptionalDependencyIsAllowed(
        bool isRequired,
        string? expectedProblem)
    {
        using TemporaryDirectory project = new();
        string dependencies = $$"""
            [
              {
                "UniqueID": "Pathoschild.ContentPatcher",
                "IsRequired": {{isRequired.ToString().ToLowerInvariant()}}
              }
            ]
            """;
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: isRequired ? "required.zip" : "optional.zip",
            dependenciesJson: dependencies);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        if (expectedProblem is null)
        {
            ProjectModStaging staging = AssertSuccessful(result);
            Assert.Empty(staging.Artifact.Manifest.RequiredDependencies);
            Assert.True(ProjectModStager.Remove(staging).Removed);
        }
        else
        {
            Assert.Null(result.Staging);
            Assert.Equal(
                expectedProblem,
                Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
            Assert.False(Directory.Exists(Path.Combine(paths.ModsPath, ModDirectoryName)));
        }
    }

    [Theory]
    [InlineData("0.0.9", null)]
    [InlineData("0.1.0", null)]
    [InlineData("0.1.1", null)]
    [InlineData("0.2.0", null)]
    [InlineData("0.2.1", null)]
    [InlineData("0.3.0", null)]
    [InlineData("0.3.1", null)]
    [InlineData("0.4.0", null)]
    [InlineData("0.4.1", null)]
    [InlineData("0.4.2", null)]
    [InlineData("0.5.0", null)]
    [InlineData("0.5.1", null)]
    [InlineData("0.5.2", "runtimeDependencyUnavailable")]
    [InlineData("999.0.0", "runtimeDependencyUnavailable")]
    public void RequiredAlwaysOnMinimumVersionMustBeProvidedByTheLab(
        string minimumVersion,
        string? expectedProblem)
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: $"always-on-{minimumVersion}.zip",
            dependenciesJson: $$"""
                [
                  {
                    "UniqueID": "SDVKit.AlwaysOn",
                    "IsRequired": true,
                    "MinimumVersion": "{{minimumVersion}}"
                  }
                ]
                """);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        if (expectedProblem is null)
        {
            ProjectModStaging staging = AssertSuccessful(result);
            ProjectModDependencyInfo dependency =
                Assert.Single(staging.Artifact.Manifest.RequiredDependencies);
            Assert.Equal("SDVKit.AlwaysOn", dependency.UniqueId);
            Assert.Equal(minimumVersion, dependency.MinimumVersion);
            Assert.True(ProjectModStager.Remove(staging).Removed);
        }
        else
        {
            Assert.Null(result.Staging);
            ProjectSmokeProblem problem = Assert.IsType<ProjectSmokeProblem>(result.Problem);
            Assert.Equal(expectedProblem, problem.Code);
            Assert.Contains(minimumVersion, problem.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("2.3", "2.3.0")]
    [InlineData("2.3-beta.1", "2.3.0-beta.1")]
    [InlineData("2.3.4-beta.1", "2.3.4-beta.1")]
    [InlineData("1.2.3+build.7", "1.2.3+build.7")]
    public void PackagedVersionIsRecordedAndCanonicalizedForTheGameMarker(
        string packageVersion,
        string expectedLaunchVersion)
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: "versioned.zip",
            sourceVersion: "%ProjectVersion%",
            packageVersion: packageVersion);
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        ProjectModStaging staging = AssertSuccessful(ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths));

        Assert.Equal(packageVersion, staging.Artifact.Manifest.Version);
        Assert.Equal(expectedLaunchVersion, staging.LaunchState.Version);
        staging.LaunchState.Validate();
        Assert.True(ProjectModStager.Remove(staging).Removed);
    }

    [Fact]
    public void PackageWithMoreThanOneManifestIsRejectedBeforeStaging()
    {
        using TemporaryDirectory project = new();
        PackageFixture fixture = CreatePackage(
            project,
            archiveName: "multiple-manifests.zip",
            additionalFiles: new Dictionary<string, string>
            {
                ["nested/manifest.json"] = Manifest("Nana.Nested", "1.0.0", "Nested.dll"),
                ["nested/Nested.dll"] = "nested assembly",
            });
        LiveLabPaths paths = LiveLabPaths.Resolve(project.Path);

        ProjectModStagingResult result = ProjectModStager.Stage(
            fixture.Package,
            fixture.Target,
            LiveLabState.SingleTopology,
            paths);

        Assert.Null(result.Staging);
        Assert.Equal("projectStagingFailed", Assert.IsType<ProjectSmokeProblem>(result.Problem).Code);
        Assert.False(Directory.Exists(Path.Combine(paths.ModsPath, ModDirectoryName)));
    }

    private static ProjectModStaging AssertSuccessful(ProjectModStagingResult result)
    {
        Assert.Null(result.Problem);
        return Assert.IsType<ProjectModStaging>(result.Staging);
    }

    private static PackageFixture CreatePackage(
        TemporaryDirectory project,
        string archiveName,
        string sourceVersion = "1.0.0",
        string packageVersion = "1.0.0",
        string assemblyContents = "packaged assembly",
        string? dependenciesJson = null,
        IReadOnlyDictionary<string, string>? additionalFiles = null,
        string modDirectoryName = ModDirectoryName)
    {
        project.WriteFile("ExampleMod.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        project.WriteFile(
            "manifest.json",
            Manifest(ModUniqueId, sourceVersion, ModEntryDll));
        string archiveRelativePath = $".sdvkit/packages/{archiveName}";
        string archivePath = Path.Combine(
            project.Path,
            archiveRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Manifest(
                ModUniqueId,
                packageVersion,
                ModEntryDll,
                dependenciesJson),
            [ModEntryDll] = assemblyContents,
        };
        if (additionalFiles is not null)
        {
            foreach ((string path, string contents) in additionalFiles)
            {
                files.Add(path, contents);
            }
        }

        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach ((string path, string contents) in files)
            {
                WriteEntry(archive, $"{modDirectoryName}/{path}", contents);
            }
        }

        string[] entries = files.Keys
            .Select(path => $"{modDirectoryName}/{path}")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifest = new ProjectManifestSummary(
            "manifest.json",
            ProjectInspectionReport.SmapiMod,
            "Example Mod",
            ModUniqueId,
            sourceVersion,
            ModEntryDll,
            null);
        var inspection = new ProjectInspectionReport(
            1,
            project.Path,
            ProjectInspectionReport.SmapiMod,
            ["ExampleMod.csproj"],
            [manifest],
            []);
        var target = new ModBuildTarget(
            inspection,
            Path.Combine(project.Path, "ExampleMod.csproj"),
            manifest);
        var package = new ProjectPackageReport(
            1,
            project.Path,
            ProjectInspectionReport.SmapiMod,
            archiveRelativePath,
            entries,
            ProjectBuilder.PackageLogPath,
            []);
        return new PackageFixture(package, target, archivePath);
    }

    private static string Manifest(
        string uniqueId,
        string version,
        string entryDll,
        string? dependenciesJson = null)
    {
        string dependencies = dependenciesJson is null
            ? string.Empty
            : $",\n  \"Dependencies\": {dependenciesJson}";
        return $$"""
            {
              "Name": "Example Mod",
              "Author": "Nana",
              "UniqueID": "{{uniqueId}}",
              "Version": "{{version}}",
              "Description": "Project smoke test mod.",
              "EntryDll": "{{entryDll}}"{{dependencies}}
            }
            """;
    }

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contents);
    }

    private static void WriteAlwaysOn(LiveLabPaths paths, string sentinel)
    {
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.AlwaysOnModPath);
        File.WriteAllText(Path.Combine(paths.AlwaysOnModPath, "sentinel.txt"), sentinel);
    }

    private static string[] ExpectedModFiles(ProjectPackageReport package)
    {
        string prefix = ModDirectoryName + "/";
        return package.Entries
            .Select(path => path[prefix.Length..])
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] FilesBelow(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.GetFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string OwnershipPath(string projectRoot, string topology)
    {
        string topologyRoot = topology == LiveLabState.SingleTopology
            ? Path.Combine(projectRoot, ".sdvkit", "lab", "single")
            : Path.Combine(projectRoot, ".sdvkit", "lab", NetworkTwoContract.Topology);
        return Path.Combine(topologyRoot, "project-smoke-staging.json");
    }

    private sealed record PackageFixture(
        ProjectPackageReport Package,
        ModBuildTarget Target,
        string ArchivePath);
}

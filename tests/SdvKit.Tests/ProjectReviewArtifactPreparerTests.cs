using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewArtifactPreparerTests
{
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
}

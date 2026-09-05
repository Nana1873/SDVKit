using System.IO.Compression;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using DotNetBuildCommand = SdvKit.Cli.DotNetBuildCommand;

namespace SdvKit.Tests;

public sealed class ProjectSelectionTests
{
    [Fact]
    public void ExplicitProjectBuildsOnlyTheSelectedManifestAndKeepsRootOutputs()
    {
        using TemporaryDirectory temporary = new();
        string selected = CreateMod(temporary.Path, "Selected");
        CreateMod(temporary.Path, "Sibling");
        Assert.Equal("projectFileAmbiguous", Assert.Single(ProjectBuilder.ResolveTarget(temporary.Path).Problems).Code);
        DotNetBuildCommand? observed = null;
        ProjectBuildReport result = ProjectBuilder.Build(temporary.Path, Ready,
            command => { observed = command; return new(0, "ok", null); }, "Selected/Selected.csproj");
        Assert.Empty(result.Problems);
        Assert.Equal("Selected/Selected.csproj", result.ProjectFile);
        Assert.NotNull(observed);
        Assert.Equal(Path.Combine(selected, "Selected.csproj"), observed.Arguments[1]);
        Assert.Equal(temporary.Path, observed.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(temporary.Path, result.Log!)));
        Assert.False(Directory.Exists(Path.Combine(selected, ".sdvkit")));
        Assert.Equal("Tests.Selected", ProjectBuilder.ResolveTarget(temporary.Path, result.ProjectFile).Target!.Manifest.UniqueId);
    }

    [Fact]
    public void ExplicitSelectionDoesNotRequireAnUnselectedSiblingsManifestToBeValid()
    {
        using TemporaryDirectory temporary = new();
        CreateMod(temporary.Path, "Selected");
        temporary.WriteFile("Sibling/manifest.json", "invalid json");
        temporary.WriteFile("Sibling/Sibling.csproj", "<Project />");
        Assert.NotEmpty(ProjectBuilder.ResolveTarget(temporary.Path).Problems);
        Assert.Empty(ProjectBuilder.ResolveTarget(temporary.Path, "Selected/Selected.csproj").Problems);
    }

    [Theory]
    [InlineData("../Outside.csproj")]
    [InlineData("missing.csproj")]
    [InlineData("Selected/manifest.json")]
    [InlineData(".sdvkit/Hidden.csproj")]
    [InlineData(".SDVKIT/Hidden.csproj")]
    public void InvalidProjectSelectionNeverBuilds(string selector)
    {
        using TemporaryDirectory temporary = new();
        CreateMod(temporary.Path, "Selected");
        temporary.WriteFile(".sdvkit/Hidden.csproj", "<Project />");
        ProjectBuildReport result = ProjectBuilder.Build(temporary.Path,
            () => throw new InvalidOperationException("No discovery for invalid selection."),
            _ => throw new InvalidOperationException("No build for invalid selection."), selector);
        Assert.Equal("projectSelectionInvalid", Assert.Single(result.Problems).Code);
    }

    [Fact]
    public void SelectedProjectCannotBorrowAnotherDirectorysManifest()
    {
        using TemporaryDirectory temporary = new();
        CreateMod(temporary.Path, "Selected");
        temporary.WriteFile("Other/Other.csproj", "<Project />");
        Assert.Equal("projectManifestMismatch", Assert.Single(ProjectBuilder.ResolveTarget(temporary.Path, "Other/Other.csproj").Problems).Code);
        Assert.Equal("projectSelectionInvalid", Assert.Single(ProjectBuilder.ResolveTarget(temporary.Path, Path.Combine(temporary.Path, "Selected/Selected.csproj")).Problems).Code);
    }

    [Fact]
    public void SelectedProjectRejectsLinkedAncestor()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TemporaryDirectory temporary = new();
        string selected = CreateMod(temporary.Path, "Selected");
        string link = Path.Combine(temporary.Path, "Linked");
        new Win32DirectChildJunctionPlatform().CreateDirectoryJunction(link, selected);
        try
        {
            Assert.Equal("reparsePointNotAllowed", Assert.Single(ProjectBuilder.ResolveTarget(temporary.Path, "Linked/Selected.csproj").Problems).Code);
        }
        finally { new Win32DirectChildJunctionPlatform().DeleteExactDirectoryJunction(link, selected); }
    }

    [Fact]
    public void ExplicitHybridPackagesThroughTheExistingBuilderButReviewRemainsUnsupported()
    {
        using TemporaryDirectory temporary = new();
        string selected = CreateMod(temporary.Path, "Selected");
        CreateMod(temporary.Path, "Sibling");
        ProjectCreator.Create(new(ProjectCreator.ContentPack, Path.Combine(selected, "Pack"), "Pack", "Tests", "Tests.Pack", "Bundled pack."));
        Assert.Equal(ProjectInspectionReport.Hybrid, ProjectBuilder.ResolveTarget(temporary.Path, "Selected/Selected.csproj").Inspection.Kind);
        ProjectPackageReport package = ProjectPackager.Package(temporary.Path, Ready, PackageRunner, "Selected/Selected.csproj");
        Assert.Empty(package.Problems);
        Assert.All(package.Entries, entry => Assert.StartsWith("Selected/", entry, StringComparison.Ordinal));
        ProjectReviewPreparationResult review = ProjectModStager.PrepareReview(temporary.Path, [], [],
            LiveLabPaths.Resolve(Directory.CreateDirectory(Path.Combine(temporary.Path, "lab")).FullName), Ready,
            _ => throw new InvalidOperationException("Hybrid review must not build."), "Selected/Selected.csproj");
        Assert.Equal("reviewProjectAmbiguous", review.Problem?.Code);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("network-2")]
    public void ReviewSelectionSurvivesBothBuildsAndOwnedStagingWithoutSelectingCompanionProject(string topology)
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "sources");
        CreateMod(root, "Selected");
        CreateMod(root, "Sibling");
        string companion = CreateMod(root, "Companion");
        string before = ModBuildIdentity.ComputeFileSet(root);
        LiveLabPaths paths = LiveLabPaths.Resolve(Directory.CreateDirectory(Path.Combine(temporary.Path, "lab")).FullName);
        var commands = new List<DotNetBuildCommand>();
        ProjectReviewPreparationResult prepared = ProjectModStager.PrepareReview(root, [companion], [], paths, Ready,
            command => { commands.Add(command); return PackageRunner(command); }, "Selected/Selected.csproj");
        Assert.Null(prepared.Problem);
        Assert.Equal(4, commands.Count);
        Assert.Equal(["Selected.csproj", "Selected.csproj", "Companion.csproj", "Companion.csproj"], commands.Select(command => Path.GetFileName(command.Arguments[1])));
        Assert.All(commands, command => Assert.Contains("-p:GamePath=C:\\SelectedGame", command.Arguments));
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
        ProjectReviewStagingResult staged = ProjectModStager.StageReview(prepared.Artifacts, topology, paths);
        Assert.Null(staged.Problem);
        ProjectReviewStagingResult read = ProjectModStager.ReadReview(paths, topology);
        Assert.Null(read.Problem);
        Assert.Equal(Path.Combine(root, "Selected", "Selected.csproj"), read.Staging!.Target.ProjectFile);
        Assert.Null(read.Staging.Artifacts.Single(artifact => artifact.Role == "companion").ProjectFile);
        foreach (ProjectReviewOwnedArtifact artifact in read.Staging.Artifacts)
            foreach (ProjectReviewRoleStagingPath rolePath in artifact.RoleStagingPaths)
                Assert.Equal(artifact.BuildIdentity, ModBuildIdentity.ComputeFileSet(rolePath.StagingPath));
        if (topology == "network-2")
        {
            LiveLabCommandResult wrong = ProjectReviewService.Execute("start", root, [companion], [], topology, paths.ProjectRoot,
                () => throw new InvalidOperationException("Mismatched selection must fail before launch."), projectFile: "Sibling/Sibling.csproj");
            Assert.Equal("reviewSetMismatch", Assert.Single(Assert.IsType<ProjectNetworkReviewReport>(wrong.Report).Problems).Code);
        }
        Assert.True(ProjectModStager.RemoveReview(paths, topology).Removed);
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    [Fact]
    public void DoctorKeepsReadyDefaultsAndExplainsIncompleteCandidates()
    {
        using TemporaryDirectory complete = new();
        complete.CreateReadyInstallation();
        using TemporaryDirectory incomplete = new();
        incomplete.WriteFile("Stardew Valley.exe", "game");
        incomplete.WriteFile("Stardew Valley.dll", "game");
        DoctorReport report = GameInstallationDiscovery.Inspect([complete.Path, incomplete.Path]);
        Assert.Equal(DoctorReport.Ready, report.Status);
        Assert.Equal(complete.Path, Assert.Single(report.Installations).GamePath);
        IncompleteInstallation candidate = Assert.Single(report.IncompleteCandidates!);
        Assert.Equal(["StardewModdingAPI.exe", "StardewModdingAPI.dll"], candidate.MissingRequirements);
        Assert.Contains("Install or repair SMAPI", Assert.Single(candidate.Actions), StringComparison.Ordinal);
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(["doctor", "--game-path", incomplete.Path, "--json"], output, error,
            () => throw new InvalidOperationException("Explicit installation must not use automatic discovery."));
        Assert.Equal(3, code);
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Equal("notFound", json.RootElement.GetProperty("status").GetString());
        Assert.Empty(json.RootElement.GetProperty("installations").EnumerateArray());
        Assert.Single(json.RootElement.GetProperty("incompleteCandidates").EnumerateArray());
    }

    [Fact]
    public void DoctorExplicitSelectionOverridesAmbiguousDiscoveryAndRejectsMissingDirectory()
    {
        using TemporaryDirectory selected = new();
        selected.CreateReadyInstallation();
        foreach (bool exists in new[] { true, false })
        {
            using StringWriter output = new();
            using StringWriter error = new();
            int code = CliApplication.Run(["doctor", "--game-path", exists ? selected.Path : Path.Combine(selected.Path, "Missing"), "--json"], output, error,
                () => new(1, DoctorReport.Ambiguous, [new("other"), new("another")]));
            Assert.Equal(exists ? 0 : 3, code);
            using JsonDocument json = JsonDocument.Parse(output.ToString());
            if (exists) Assert.Equal(selected.Path, json.RootElement.GetProperty("installations")[0].GetProperty("gamePath").GetString());
            else Assert.Equal(4, json.RootElement.GetProperty("incompleteCandidates")[0].GetProperty("missingRequirements").GetArrayLength());
        }
    }

    [Fact]
    public void CliForwardsProjectSelectionAndRejectsItForReviewStatus()
    {
        using StringWriter output = new();
        using StringWriter error = new();
        string? selector = null;
        int result = CliApplication.Run(["project", "review", "start", "root", "--project", "Selected/Selected.csproj", "--json"], output, error, Ready,
            runProjectReview: (_, root, companions, _, _, _, _, project) =>
            {
                Assert.Equal("root", root);
                Assert.Empty(companions);
                selector = project;
                return new(0, new { state = "test" });
            });
        Assert.Equal(0, result);
        Assert.Equal("Selected/Selected.csproj", selector);
        result = CliApplication.Run(["project", "review", "status", "--project", "Selected.csproj", "--json"], output, error, Ready);
        Assert.Equal(2, result);
    }

    private static DoctorReport Ready() => new(1, DoctorReport.Ready, [new("C:\\SelectedGame")]);

    private static string CreateMod(string root, string name)
    {
        string path = Path.Combine(root, name);
        Assert.Empty(ProjectCreator.Create(new(ProjectCreator.SmapiMod, path, name, "Tests", $"Tests.{name}", "Original selection test mod.")).Problems);
        return path;
    }

    private static DotNetBuildResult PackageRunner(DotNetBuildCommand command)
    {
        if (command.Arguments.Contains("-p:EnableModZip=true"))
        {
            string root = Path.GetDirectoryName(command.Arguments[1])!;
            string name = Path.GetFileNameWithoutExtension(command.Arguments[1]);
            string zipPath = command.Arguments.Single(argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal))["-p:ModZipPath=".Length..];
            using ZipArchive archive = ZipFile.Open(Path.Combine(zipPath, name + ".zip"), ZipArchiveMode.Create);
            void Write(string path, string value) { using StreamWriter writer = new(archive.CreateEntry(path).Open()); writer.Write(value); }
            Write($"{name}/manifest.json", File.ReadAllText(Path.Combine(root, "manifest.json")));
            Write($"{name}/{name}.dll", "selected built assembly");
            if (Directory.Exists(Path.Combine(root, "Pack")))
            {
                Write($"{name}/Pack/manifest.json", File.ReadAllText(Path.Combine(root, "Pack", "manifest.json")));
                Write($"{name}/Pack/content.json", "{}");
            }
        }
        return new(0, "ok", null);
    }
}

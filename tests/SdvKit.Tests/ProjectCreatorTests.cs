using System.Text.Json;
using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class ProjectCreatorTests
{
    [Fact]
    public void CreatesOnlyTheMinimalSmapiProjectFiles()
    {
        using TemporaryDirectory temporary = new();
        string target = System.IO.Path.Combine(temporary.Path, "ExampleMod");

        ProjectCreationReport report = ProjectCreator.Create(Request(
            ProjectCreator.SmapiMod,
            target,
            "Nana.ExampleMod"));

        Assert.Empty(report.Problems);
        Assert.Equal(ProjectInspectionReport.SmapiMod, report.Kind);
        Assert.Equal(
            [".gitignore", "ExampleMod.csproj", "manifest.json", "ModEntry.cs"],
            report.Files);
        Assert.Equal(report.Files, FilesIn(target));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit")));

        string project = File.ReadAllText(System.IO.Path.Combine(target, "ExampleMod.csproj"));
        Assert.Contains("<TargetFramework>net6.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("Pathoschild.Stardew.ModBuildConfig", project, StringComparison.Ordinal);
        Assert.Contains("Version=\"4.4.0\"", project, StringComparison.Ordinal);
        Assert.Contains("<EnableModDeploy>false</EnableModDeploy>", project, StringComparison.Ordinal);
        Assert.Contains("<EnableModZip>false</EnableModZip>", project, StringComparison.Ordinal);

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            System.IO.Path.Combine(target, "manifest.json")));
        Assert.Equal("Nana.ExampleMod", manifest.RootElement.GetProperty("UniqueID").GetString());
        Assert.Equal("ExampleMod.dll", manifest.RootElement.GetProperty("EntryDll").GetString());
        Assert.Equal("4.0.0", manifest.RootElement.GetProperty("MinimumApiVersion").GetString());
        Assert.Equal(
            ProjectInspectionReport.SmapiMod,
            ProjectInspector.Inspect(target).Kind);
    }

    [Fact]
    public void CreatesOnlyTheMinimalContentPatcherFiles()
    {
        using TemporaryDirectory temporary = new();
        string target = System.IO.Path.Combine(temporary.Path, "ExamplePack");

        ProjectCreationReport report = ProjectCreator.Create(Request(
            ProjectCreator.ContentPack,
            target,
            "Nana.ExamplePack"));

        Assert.Empty(report.Problems);
        Assert.Equal(ProjectInspectionReport.ContentPack, report.Kind);
        Assert.Equal([".gitignore", "content.json", "manifest.json"], report.Files);
        Assert.Equal(report.Files, FilesIn(target));
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit")));

        using JsonDocument content = JsonDocument.Parse(File.ReadAllText(
            System.IO.Path.Combine(target, "content.json")));
        Assert.Equal("2.9.0", content.RootElement.GetProperty("Format").GetString());
        Assert.Empty(content.RootElement.GetProperty("Changes").EnumerateArray());
        ProjectManifestSummary manifest = Assert.Single(ProjectInspector.Inspect(target).Manifests);
        Assert.Equal("Pathoschild.ContentPatcher", manifest.ContentPackFor);
    }

    [Fact]
    public void ExistingContentIsNeverOverwritten()
    {
        using TemporaryDirectory temporary = new();
        string target = System.IO.Path.Combine(temporary.Path, "Existing");
        Directory.CreateDirectory(target);
        string sentinel = System.IO.Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "keep me");

        ProjectCreationReport report = ProjectCreator.Create(Request(
            ProjectCreator.SmapiMod,
            target,
            "Nana.Existing"));

        Assert.Equal("targetNotEmpty", Assert.Single(report.Problems).Code);
        Assert.Equal("keep me", File.ReadAllText(sentinel));
        Assert.Equal(["keep.txt"], FilesIn(target));
    }

    private static ProjectCreationRequest Request(string kind, string path, string uniqueId)
    {
        return new ProjectCreationRequest(
            kind,
            path,
            "Example project",
            "Nana",
            uniqueId,
            "A minimal example project.");
    }

    private static string[] FilesIn(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

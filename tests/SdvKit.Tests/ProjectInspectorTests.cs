using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class ProjectInspectorTests
{
    [Fact]
    public void EntryDllManifestClassifiesASmapiMod()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        temporary.WriteFile("manifest.json", Manifest(
            uniqueId: "Nana.Example",
            entryDll: "Example.dll"));

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(ProjectInspectionReport.SmapiMod, report.Kind);
        Assert.Equal(["Example.csproj"], report.ProjectFiles);
        ProjectManifestSummary manifest = Assert.Single(report.Manifests);
        Assert.Equal("manifest.json", manifest.Path);
        Assert.Equal(ProjectInspectionReport.SmapiMod, manifest.Kind);
        Assert.Equal("Nana.Example", manifest.UniqueId);
        Assert.Equal("Example.dll", manifest.EntryDll);
        Assert.Null(manifest.ContentPackFor);
        Assert.Empty(report.Problems);
        Assert.False(Directory.Exists(System.IO.Path.Combine(temporary.Path, ".sdvkit")));
    }

    [Fact]
    public void ContentPackWithAnUnrelatedProjectFileStaysAContentPack()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        temporary.WriteFile("manifest.json", Manifest(
            uniqueId: "Nana.ExamplePack",
            contentPackFor: "Pathoschild.ContentPatcher"));

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.ContentPack, report.Kind);
        Assert.Equal(["tools/Helper.csproj"], report.ProjectFiles);
        ProjectManifestSummary manifest = Assert.Single(report.Manifests);
        Assert.Equal(ProjectInspectionReport.ContentPack, manifest.Kind);
        Assert.Equal("Pathoschild.ContentPatcher", manifest.ContentPackFor);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void SeparateCodeAndContentPackManifestsClassifyAHybrid()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        temporary.WriteFile("manifest.json", Manifest(
            uniqueId: "Nana.Example",
            entryDll: "Example.dll"));
        temporary.WriteFile("assets/Pack/manifest.json", Manifest(
            uniqueId: "Nana.Example.Pack",
            contentPackFor: "Pathoschild.ContentPatcher"));

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Hybrid, report.Kind);
        Assert.Equal(
            ["assets/Pack/manifest.json", "manifest.json"],
            report.Manifests.Select(manifest => manifest.Path));
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void BothTypeFieldsInOneManifestAreInvalidInsteadOfHybrid()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", Manifest(
            uniqueId: "Nana.Invalid",
            entryDll: "Invalid.dll",
            contentPackFor: "Pathoschild.ContentPatcher"));

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("invalidManifest", problem.Code);
        Assert.Equal("manifest.json", problem.Path);
    }

    [Fact]
    public void BuildDirectoriesDoNotAffectClassification()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", Manifest(
            uniqueId: "Nana.ExamplePack",
            contentPackFor: "Pathoschild.ContentPatcher"));
        temporary.WriteFile("bin/manifest.json", "{ not valid json }");
        temporary.WriteFile("OBJ/Generated.csproj", "not a project");

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.ContentPack, report.Kind);
        Assert.Empty(report.ProjectFiles);
        Assert.Single(report.Manifests);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void MalformedManifestReturnsAControlledProblem()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", "{ not valid json }");

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("invalidManifest", problem.Code);
        Assert.Equal("manifest.json", problem.Path);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", """
            {
              // SMAPI manifests permit comments.
              "Name": "Example",
              "Author": "Nana",
              "UniqueID": "Nana.Example",
              "Version": "1.0.0",
              "Description": "Example mod.",
              "EntryDll": "Example.dll",
            }
            """);

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.SmapiMod, report.Kind);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void ProjectFileWithoutAManifestStaysUnknown()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        Assert.Equal(["Example.csproj"], report.ProjectFiles);
        Assert.Equal("manifestNotFound", Assert.Single(report.Problems).Code);
    }

    [Theory]
    [InlineData("Nana Example", "Example.dll")]
    [InlineData("Nana.Example", "Example.txt")]
    [InlineData("Nana.Example", ".dll")]
    public void InvalidIdentityOrEntryDllReturnsAProblem(string uniqueId, string entryDll)
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", Manifest(uniqueId, entryDll));

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        Assert.Equal("invalidManifest", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void InvalidSemanticVersionReturnsAProblem()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", Manifest(
            "Nana.Example",
            entryDll: "Example.dll",
            version: "1.0.0--"));

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        Assert.Equal("invalidManifest", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void MissingRequiredIdentityFieldReturnsAProblem()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", """
            {
              "Name": "Example",
              "UniqueID": "Nana.Example",
              "Version": "1.0.0",
              "Description": "Example mod.",
              "EntryDll": "Example.dll"
            }
            """);

        ProjectInspectionReport report = ProjectInspector.Inspect(temporary.Path);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        Assert.Equal("invalidManifest", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void MissingPathReturnsAControlledProblem()
    {
        using TemporaryDirectory temporary = new();
        string missing = System.IO.Path.Combine(temporary.Path, "missing");

        ProjectInspectionReport report = ProjectInspector.Inspect(missing);

        Assert.Equal(ProjectInspectionReport.Unknown, report.Kind);
        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("pathNotFound", problem.Code);
        Assert.Null(problem.Path);
    }

    private static string Manifest(
        string uniqueId,
        string? entryDll = null,
        string? contentPackFor = null,
        string version = "1.0.0")
    {
        string typeProperty = entryDll is not null
            ? $",\n  \"EntryDll\": \"{entryDll}\""
            : string.Empty;
        if (contentPackFor is not null)
        {
            typeProperty += $",\n  \"ContentPackFor\": {{ \"UniqueID\": \"{contentPackFor}\" }}";
        }

        return $$"""
            {
              "Name": "Example",
              "Author": "Nana",
              "UniqueID": "{{uniqueId}}",
              "Version": "{{version}}",
              "Description": "Example project."{{typeProperty}}
            }
            """;
    }
}

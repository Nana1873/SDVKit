using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectCheckerTests : IDisposable
{
    private readonly string root;
    private readonly List<string> directoryLinks = [];

    public ProjectCheckerTests()
    {
        DirectoryInfo repository = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "SDVKit.sln")))
        {
            repository = repository.Parent ?? throw new InvalidOperationException("Test checkout not found.");
        }
        root = Path.Combine(repository.FullName, ".sdvkit", "project-check-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [Theory]
    [InlineData(ProjectCreator.SmapiMod, 1)]
    [InlineData(ProjectCreator.ContentPack, 2)]
    public void GeneratedProjectsPassWithoutDiscoveryAndRemainUnchanged(string kind, int count)
    {
        Create(kind);
        Write("i18n/default.json", "{ // comment\n \"hello\": \"Hello {{name}}!\", }");
        Write("i18n/de.json", "{ /* comment */ \"hello\": \"Hallo {{name}}!\", }");
        Dictionary<string, string> before = Snapshot();
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exit = CliApplication.Run(["project", "check", root, "--json"], output, error,
            () => throw new InvalidOperationException("Offline check must never discover a game."));

        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        Assert.Equal("passed", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(count + 2, result.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("Version", "1.2.3-beta.2", true)]
    [InlineData("Version", "%ProjectVersion%", true)]
    [InlineData("Version", "01.2.3", false)]
    [InlineData("Version", "1.2.3+build", false)]
    [InlineData("UniqueID", "Author.Mod-name_1", true)]
    [InlineData("UniqueID", "Author Mod", false)]
    [InlineData("EntryDll", "../escape.dll", false)]
    public void OfficialManifestRegexRulesAreApplied(string field, string value, bool valid)
    {
        Create();
        Edit("manifest.json", obj => obj[field] = value);
        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Equal(valid ? "passed" : "failed", result.Status);
        if (!valid)
        {
            Assert.Contains(result.Problems, problem => problem.File == "manifest.json" && problem.Field == "/" + field);
        }
    }

    [Theory]
    [InlineData("nExUs:123", true)]
    [InlineData("GitHub:Some.Author/repository", true)]
    [InlineData("Nexus:not-a-number", false)]
    public void InlineCaseInsensitiveUpdateKeyRegexWorks(string key, bool valid)
    {
        Create();
        Edit("manifest.json", obj => obj["UpdateKeys"] = new JsonArray(key));
        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Equal(valid ? "passed" : "failed", result.Status);
        if (!valid)
        {
            Assert.Contains(result.Problems, problem => problem.Field == "/UpdateKeys/0" && problem.Code == "schemaViolation");
        }
    }

    [Fact]
    public void ManifestOneOfAndAdditionalPropertiesAreEnforced()
    {
        Create();
        Edit("manifest.json", obj =>
        {
            obj["ContentPackFor"] = new JsonObject { ["UniqueID"] = "Pathoschild.ContentPatcher" };
            obj["Typo"] = true;
        });
        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Contains(result.Problems, problem => problem.Message.StartsWith("oneOf:", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Field == "/Typo");
    }

    [Theory]
    [InlineData("{\"Format\":\"2.9.0\",\"Changes\":[{\"Action\":\"Include\"}]}", "/Changes/0", "FromFile")]
    [InlineData("{\"Format\":\"2.9.0\",\"Changes\":[{\"Action\":\"BadAction\"}]}", "/Changes/0/Action", "enum")]
    [InlineData("{\"Format\":\"2.9.0\",\"Changes\":[{\"Action\":\"Load\",\"Target\":\"CONTENT/test.XNB\",\"FromFile\":\"a.json\"}]}", "/Changes/0/Target", "not")]
    [InlineData("{\"Format\":\"2.9.0\",\"Changes\":[{\"Action\":\"Load\",\"Target\":\"Data/Test\",\"FromFile\":\"assets../outside.json\"}]}", "/Changes/0/FromFile", "not")]
    public void ContentPatcherDraft7ConditionsAndRegexGiveActualFields(string json, string field, string message)
    {
        Create(ProjectCreator.ContentPack);
        Write("content.json", json);
        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Equal("failed", result.Status);
        Assert.Contains(result.Problems, problem => problem.File == "content.json" && problem.Field == field
            && problem.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicPathsAndNestedProjectsAreNotRead()
    {
        Create(ProjectCreator.ContentPack);
        Write("content.json", """
            { "Format": "2.9.0", "Changes": [
              { "Action": "Include", "FromFile": "assets/{{Season}}.json" },
              { "Action": "Include", "FromFile": "assets/static.json" },
              { "Action": "Load", "Target": "Data/Test", "FromFile": "absent.json" }
            ] }
            """);
        Write("assets/static.json", "broken JSON");
        Write("nested/manifest.json", "broken JSON");
        Write("i18n/default.json", "{}");
        Write("i18n/nested/de.json", "broken JSON");

        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Empty(result.Problems);
        Assert.Equal(["manifest.json", "content.json", "i18n/default.json"], result.Files.Select(file => file.File));
    }

    [Theory]
    [InlineData("manifest.json", "{\n bad", "invalidJson", "")]
    [InlineData("i18n/default.json", "{\"hello\": 4}", "schemaViolation", "/hello")]
    [InlineData("i18n/default.json", "{\"hello\":null}", "schemaViolation", "/hello")]
    [InlineData("i18n/default.json", "{\"a/b~c\": [], \"hello\":\"a\",\"hello\":\"b\"}", "duplicateProperty", "/hello")]
    [InlineData("i18n/default.json", "{\"a/b~c\": []}", "schemaViolation", "/a~1b~0c")]
    public void JsonAndTranslationErrorsIdentifySource(string file, string json, string code, string field)
    {
        Create();
        Write(file, json);
        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Contains(result.Problems, problem => problem.File == file && problem.Code == code
            && (code == "invalidJson" ? problem.Message.Contains("line 2", StringComparison.Ordinal) : problem.Field == field));
    }

    [Fact]
    public void MissingFilesAndUnsupportedProviderAreExplicit()
    {
        Assert.Contains(ProjectChecker.Check(root).Problems, p => p.File == "manifest.json" && p.Code == "fileNotFound");
        Create(ProjectCreator.ContentPack);
        File.Delete(Path.Combine(root, "content.json"));
        Write("i18n/de.json", "{}");
        ProjectCheckReport result = ProjectChecker.Check(root);
        Assert.Contains(result.Problems, p => p.File == "content.json" && p.Code == "fileNotFound");
        Assert.Contains(result.Problems, p => p.File == "i18n/default.json" && p.Code == "fileNotFound");
        Edit("manifest.json", obj => obj["ContentPackFor"]!["UniqueID"] = "Other.Provider");
        Assert.Contains(ProjectChecker.Check(root).Problems, p => p.Code == "unsupportedProvider");
    }

    [Theory]
    [InlineData("https://example.invalid/custom-schema.json")]
    [InlineData("../../outside.json")]
    [InlineData("https://smapi.io/schemas/i18n.json")]
    public void UnknownOrMismatchedSchemaIsNeverFollowed(string declaration)
    {
        Create();
        Edit("manifest.json", obj => obj["$schema"] = declaration);
        ProjectCheckProblem problem = Assert.Single(ProjectChecker.Check(root).Problems);
        Assert.Equal("unsupportedSchema", problem.Code);
        Assert.Equal("/$schema", problem.Field);
    }

    [Fact]
    public void CanonicalSchemaDeclarationsPassAndOtherCpVersionsAreUnsupported()
    {
        Create(ProjectCreator.ContentPack);
        Edit("manifest.json", obj => obj["$schema"] = "https://smapi.io/schemas/manifest.json");
        Edit("content.json", obj => obj["$schema"] = "https://smapi.io/schemas/content-patcher.json");
        Write("i18n/default.json", "{\"$schema\":\"https://smapi.io/schemas/i18n.json\"}");
        Assert.Empty(ProjectChecker.Check(root).Problems);
        Edit("content.json", obj => obj["Format"] = "3.0.0");
        Assert.Contains(ProjectChecker.Check(root).Problems, p => p.Code == "unsupportedFormat" && p.Field == "/Format");
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("content.json")]
    [InlineData("i18n/default.json")]
    public void FileLinksAreRejectedWithoutReadingTargetsWhenSupported(string file)
    {
        Create(ProjectCreator.ContentPack);
        Write("i18n/default.json", "{}");
        string outside = Write("outside.json", "malformed; must not be read");
        string linked = Path.Combine(root, file);
        File.Delete(linked);
        try
        {
            File.CreateSymbolicLink(linked, outside);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // Windows needs Developer Mode or SeCreateSymbolicLinkPrivilege; junction tests still run.
            return;
        }
        Assert.Contains(ProjectChecker.Check(root).Problems, p => p.File == file && p.Code == "linkedPath");
        Assert.Equal("malformed; must not be read", File.ReadAllText(outside));
    }

    [Fact]
    public void DirectoryLinksAndLinkedRootAncestorsAreRejected()
    {
        Create();
        string target = Path.Combine(root, "outside");
        Directory.CreateDirectory(target);
        CreateDirectoryLink(Path.Combine(root, "i18n"), target);
        Assert.Contains(ProjectChecker.Check(root).Problems, p => p.File == "i18n" && p.Code == "linkedPath");
        string alias = Path.Combine(root, "alias");
        CreateDirectoryLink(alias, target);
        Assert.Contains(ProjectChecker.Check(alias).Problems, p => p.Code == "linkedPath");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        Assert.Contains(ProjectChecker.Check(Path.Combine(alias, "child")).Problems, p => p.Code == "linkedPath");
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("content.json")]
    [InlineData("i18n/default.json")]
    public void JunctionAtAuthoringFilePathIsRejected(string relative)
    {
        Create(ProjectCreator.ContentPack);
        Write("i18n/default.json", "{}");
        string target = Path.Combine(root, "outside");
        Directory.CreateDirectory(target);
        string path = Path.Combine(root, relative);
        File.Delete(path);
        CreateDirectoryLink(path, target);
        Assert.Contains(ProjectChecker.Check(root).Problems, p => p.File == relative && p.Code == "linkedPath");
    }

    private void CreateDirectoryLink(string path, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            new Win32DirectChildJunctionPlatform().CreateDirectoryJunction(path, target);
        }
        else
        {
            Directory.CreateSymbolicLink(path, target);
        }
        directoryLinks.Add(path);
    }

    [Theory]
    [InlineData("manifest", "07AE602F4C9E76DF1CA38300002438D27EEE7100FFA0D25D97386F2198FCD034")]
    [InlineData("content-patcher", "E8228D81C13E0B8721EA16A8885CFDA9B59406CE3FDF492E6430D0F32FC41995")]
    [InlineData("i18n", "FC2891224A73612CEBDF62E1D27E4058E909C2D82B660AD0766361917D394686")]
    public void DistributedSchemasMatchPinnedUpstreamBytes(string name, string hash)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "schemas", name + ".json"));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void HumanOutputAndExitCodesAreUseful()
    {
        Create();
        Edit("manifest.json", obj => obj["UniqueID"] = "Bad ID");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(3, CliApplication.Run(["project", "check", root], output, error));
        Assert.Contains("manifest.json /UniqueID", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("do not prove", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, CliApplication.Run(["project", "check", root, "--unknown"], output, error));
        Assert.Equal(2, CliApplication.Run(["project", "check", root, "--json", "--json"], output, error));
        Assert.Equal(0, CliApplication.Run(["project", "check", "--help"], output, error));
        Assert.Contains("Offline schema check", output.ToString(), StringComparison.Ordinal);
    }

    private void Create(string kind = ProjectCreator.SmapiMod)
    {
        Assert.Empty(ProjectCreator.Create(new(kind, root, "Checked Mod", "SDVKit", "SDVKit.CheckedMod", "Test authoring files.")).Problems);
    }

    private string Write(string relative, string content)
    {
        string file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    private void Edit(string relative, Action<JsonObject> edit)
    {
        JsonObject obj = JsonNode.Parse(File.ReadAllText(Path.Combine(root, relative)))!.AsObject();
        edit(obj);
        Write(relative, obj.ToJsonString());
    }

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

    public void Dispose()
    {
        foreach (string link in directoryLinks)
        {
            File.SetAttributes(link, FileAttributes.Normal);
            Directory.Delete(link);
        }
        Directory.Delete(root, recursive: true);
    }
}

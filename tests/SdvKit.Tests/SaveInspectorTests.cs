using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class SaveInspectorTests
{
    private const string Minimal = "<SaveGame><gameVersion>1.6.15</gameVersion><player><money>500</money><health>100</health></player><year>1</year><dayOfMonth>1</dayOfMonth><currentSeason>spring</currentSeason><whichFarm>0</whichFarm></SaveGame>";

    [Fact]
    public void SourceReadOnlyHandleAndCopyIdentityArePreserved()
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source", Minimal);
        byte[] before = File.ReadAllBytes(source);
        using var readOnly = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        SaveInspection result = SaveInspector.Inspect(temp.Path, source);
        Assert.Equal(500m, result.Player["money"]);
        Assert.Null(result.Player["stamina"]);
        Assert.Equal(1, result.World["year"]);
        Assert.False(result.FarmAvailable);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(before.Length, result.Bytes);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, result.Copy)));
        Assert.DoesNotContain(temp.Path, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsingUsesTheIsolatedCopyEvenWhenSourceChangesAfterCopy()
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source", Minimal);
        SaveInspection result = SaveInspector.Inspect(temp.Path, source, _ => File.WriteAllText(source, "changed"));
        Assert.Equal(500m, result.Player["money"]);
        Assert.Equal(Minimal, File.ReadAllText(Path.Combine(temp.Path, result.Copy)));
        Assert.Equal("changed", File.ReadAllText(source));
    }

    [Fact]
    public void ChangedCopyIsRejectedWithoutChangingSource()
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source", Minimal);
        var error = Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source,
            copy => File.WriteAllText(copy, Minimal.Replace("500", "501", StringComparison.Ordinal))));
        Assert.Contains("copyChanged", error.Message, StringComparison.Ordinal);
        Assert.Equal(Minimal, File.ReadAllText(source));
    }

    [Theory]
    [InlineData("<SaveGame>")]
    [InlineData("<!DOCTYPE SaveGame [<!ENTITY e SYSTEM 'file:///not-selected'>]><SaveGame>&e;</SaveGame>")]
    public void MalformedAndExternalEntitiesFailAfterCopy(string xml)
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source", xml);
        Assert.Throws<XmlException>(() => SaveInspector.Inspect(temp.Path, source));
        Assert.Equal(xml, File.ReadAllText(source));
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, ".sdvkit/save-inspection"), "save.xml", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("<SaveGameInfo/>", "schemaUnavailable")]
    [InlineData("<SaveGame/>", "versionUnavailable")]
    [InlineData("<SaveGame><gameVersion>1.5.6</gameVersion></SaveGame>", "unsupportedVersion")]
    [InlineData("<SaveGame><gameVersion>1.7.0</gameVersion></SaveGame>", "unsupportedVersion")]
    [InlineData("<SaveGame><gameVersion>1.6.15</gameVersion><year>NaN</year></SaveGame>", "invalidNumber")]
    [InlineData("<SaveGame><gameVersion>1.6.15</gameVersion><year>1</year><year>2</year></SaveGame>", "ambiguousField")]
    public void UnsupportedOrAmbiguousSchemasFailWithActionableCode(string xml, string code)
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source", xml);
        Assert.Contains(code, Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsAreOrderedAndBoundedWithStableTileIdentities()
    {
        using TemporaryDirectory temp = new();
        string items = string.Concat(Enumerable.Range(0, 101).Reverse().Select(i => Object(i)));
        string xml = Minimal.Replace("</SaveGame>", $"<locations><GameLocation><name>Farm</name><objects>{items}</objects><buildings><Building><tileX>3</tileX><tileY>4</tileY><buildingType>Barn</buildingType></Building></buildings></GameLocation></locations></SaveGame>", StringComparison.Ordinal);
        SaveInspection result = SaveInspector.Inspect(temp.Path, temp.WriteFile("source", xml));
        Assert.True(result.FarmAvailable);
        Assert.Equal(101, result.ObjectCount);
        Assert.Equal(100, result.Objects.Count);
        Assert.Equal(Enumerable.Range(0, 100), result.Objects.Select(o => o.X));
        Assert.Equal(new SaveBuilding(3, 4, "Barn"), Assert.Single(result.Buildings));
    }

    [Fact]
    public void SizeDepthAndRecordLimitsFailClosed()
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source");
        using (var file = File.OpenWrite(source)) file.SetLength(SaveInspector.MaximumBytes + 1L);
        Assert.Contains("sizeLimit", Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source)).Message, StringComparison.Ordinal);
        File.WriteAllText(source, string.Concat(Enumerable.Repeat("<a>", 66)) + string.Concat(Enumerable.Repeat("</a>", 66)));
        Assert.Contains("xmlLimit", Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source)).Message, StringComparison.Ordinal);
        File.WriteAllText(source, Minimal.Replace("</SaveGame>", "<locations><GameLocation><name>Farm</name><objects>" + string.Concat(Enumerable.Range(0, 10001).Select(i => Object(i))) + "</objects></GameLocation></locations></SaveGame>", StringComparison.Ordinal));
        Assert.Contains("recordLimit", Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedAncestorsAndHardLinksAreRejected()
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("real/source", Minimal);
        string link = Path.Combine(temp.Path, "linked");
        var junction = new Win32DirectChildJunctionPlatform();
        junction.CreateDirectoryJunction(link, Path.GetDirectoryName(source)!);
        try
        {
            Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, Path.Combine(link, "source")));
            Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(link, source));
        }
        finally { junction.DeleteExactDirectoryJunction(link, Path.GetDirectoryName(source)!); }
        Assert.True(CreateHardLink(source + ".link", source, IntPtr.Zero));
        Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source));
        Assert.Equal(Minimal, File.ReadAllText(source));
    }

    [Fact]
    public void OutputJunctionAndTraversalAreRejectedBeforeCopy()
    {
        using TemporaryDirectory temp = new();
        string source = temp.WriteFile("source", Minimal);
        Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, Path.Combine(temp.Path, "child/../source")));
        string link = Path.Combine(temp.Path, ".sdvkit");
        string foreign = Path.Combine(temp.Path, "foreign");
        Directory.CreateDirectory(foreign);
        var junction = new Win32DirectChildJunctionPlatform();
        junction.CreateDirectoryJunction(link, foreign);
        try { Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, source)); }
        finally { junction.DeleteExactDirectoryJunction(link, foreign); }
        Assert.Empty(Directory.GetFileSystemEntries(foreign));
    }

    [Fact]
    public void FixtureRequiresRegisteredMarkerAndCopiedPayloadIdentity()
    {
        using TemporaryDirectory temp = new();
        LiveLabPaths paths = LiveLabPaths.Resolve(temp.Path);
        var identity = new TestSaveIdentity(1, new string('a', 32), new string('b', 32), 123, "SDVKit_123", "SDVKit", "SDVKit", "Tests");
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        temp.WriteFile(Path.GetRelativePath(temp.Path, paths.TestSaveManifestPath), JsonSerializer.Serialize(identity, jsonOptions));
        string payload = Path.GetRelativePath(temp.Path, paths.TestSaveBaselinePath);
        temp.WriteFile(payload + "/" + TestSaveContract.FixtureMarkerFileName, JsonSerializer.Serialize(identity, jsonOptions));
        temp.WriteFile(payload + "/SaveGameInfo", "not parsed");
        temp.WriteFile(payload + "/" + identity.SaveId, Minimal);
        var store = new TestSaveFixtureStore(paths);
        string selected = store.SelectInspectionSource("baseline", out TestSaveIdentity registered);
        Assert.Equal(identity, registered);
        Assert.Contains("fixtureMismatch", Assert.Throws<InvalidDataException>(() => SaveInspector.Inspect(temp.Path, selected, fixture: registered)).Message, StringComparison.Ordinal);
        string bound = Minimal.Replace("<player>", $"<uniqueIDForThisGame>123</uniqueIDForThisGame><player><modData><item><key><string>SDVKit/WorkspaceOwnerId</string></key><value><string>{identity.WorkspaceOwnerId}</string></value></item><item><key><string>SDVKit/FixtureId</string></key><value><string>{identity.FixtureId}</string></value></item></modData>", StringComparison.Ordinal);
        File.WriteAllText(selected, bound);
        Assert.Equal(500m, SaveInspector.Inspect(temp.Path, selected, fixture: registered).Player["money"]);
        File.WriteAllText(Path.Combine(paths.TestSaveBaselinePath, TestSaveContract.FixtureMarkerFileName), JsonSerializer.Serialize(identity with { FixtureId = new string('c', 32) }, jsonOptions));
        Assert.Throws<InvalidDataException>(() => store.SelectInspectionSource("baseline", out _));
    }

    [Fact]
    public void CliListsSectionsAndRejectsImplicitSourcesWithoutPersonalPathDisclosure()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["save", "sections", "--json"], output, error));
        Assert.Contains("money", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, CliApplication.Run(["save", "inspect", "--json"], output, error));
        output.GetStringBuilder().Clear();
        Assert.Equal(3, CliApplication.Run(["save", "inspect", "--source", "Z:/private-person/no-such-file", "--json"], output, error));
        Assert.DoesNotContain("private-person", output.ToString(), StringComparison.Ordinal);
    }

    private static string Object(int x) => $"<item><key><Vector2><X>{x}</X><Y>1</Y></Vector2></key><value><Object><itemId>388</itemId><stack>1</stack></Object></value></item>";

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}

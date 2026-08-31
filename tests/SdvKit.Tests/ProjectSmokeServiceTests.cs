using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectSmokeServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ContentPackIsUnsupportedBeforeDoctorOrStateCreation()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("manifest.json", ContentPackManifest());
        source.WriteFile("content.json", "{ \"Format\": \"2.9.0\", \"Changes\": [] }");
        var doctorCalled = false;

        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Doctor should not run.");
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("unsupported", report.State);
        Assert.Equal("unsupportedProjectKind", Assert.Single(report.Problems).Code);
        Assert.False(doctorCalled);
        AssertNoProjectState(source, lab);
    }

    [Fact]
    public void HybridIsUnsupportedBeforeDoctorOrStateCreation()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("Example.csproj", MinimalProject());
        source.WriteFile("manifest.json", CodeManifest());
        source.WriteFile("assets/Pack/manifest.json", ContentPackManifest("Nana.Example.Pack"));
        var doctorCalled = false;

        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Doctor should not run.");
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("unsupported", report.State);
        Assert.Equal("unsupportedProjectKind", Assert.Single(report.Problems).Code);
        Assert.False(doctorCalled);
        AssertNoProjectState(source, lab);
    }

    [Fact]
    public void RequiredRuntimeDependencyIsRejectedWithoutDoctorOrSideEffects()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("Example.csproj", MinimalProject());
        source.WriteFile("manifest.json", CodeManifest(
            """
            [
              { "UniqueID": "Pathoschild.ContentPatcher", "IsRequired": true }
            ]
            """));
        var doctorCalled = false;

        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Doctor should not run.");
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("unsupported", report.State);
        ProjectSmokeProblem problem = Assert.Single(report.Problems);
        Assert.Equal("runtimeDependencyUnavailable", problem.Code);
        Assert.Equal("manifest.json", problem.Path);
        Assert.Contains("Pathoschild.ContentPatcher", problem.Message, StringComparison.Ordinal);
        Assert.Contains("does not acquire dependencies", problem.Message, StringComparison.Ordinal);
        Assert.False(doctorCalled);
        AssertNoProjectState(source, lab);
    }

    [Fact]
    public void UnavailableAlwaysOnMinimumVersionIsRejectedBeforeDoctor()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("Example.csproj", MinimalProject());
        source.WriteFile("manifest.json", CodeManifest(
            """
            [
              {
                "UniqueID": "SDVKit.AlwaysOn",
                "IsRequired": true,
                "MinimumVersion": "999.0.0"
              }
            ]
            """));
        var doctorCalled = false;

        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Doctor should not run.");
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("unsupported", report.State);
        ProjectSmokeProblem problem = Assert.Single(report.Problems);
        Assert.Equal("runtimeDependencyUnavailable", problem.Code);
        Assert.Contains("SDVKit.AlwaysOn >= 999.0.0", problem.Message, StringComparison.Ordinal);
        Assert.False(doctorCalled);
        AssertNoProjectState(source, lab);
    }

    [Theory]
    [InlineData("[SMAPI] Author.Mod failed", "Author.Mod", true, true)]
    [InlineData("[SMAPI] Author.Mod.Extra failed", "Author.Mod", true, false)]
    [InlineData("[SMAPI] Foo-Bar failed", "Foo", true, false)]
    [InlineData("[SMAPI] Example Mod failed", "Example Mod", false, true)]
    public void TargetLogTokensRequireTheCorrectBoundaries(
        string line,
        string token,
        bool identityToken,
        bool expected)
    {
        Assert.Equal(
            expected,
            ProjectSmokeService.ContainsDelimitedToken(line, token, identityToken));
    }

    [Fact]
    public void OptionalRuntimeDependencyReachesTheControlledDoctorGate()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("Example.csproj", MinimalProject());
        source.WriteFile("manifest.json", CodeManifest(
            """
            [
              { "UniqueID": "Pathoschild.ContentPatcher", "IsRequired": false }
            ]
            """));
        var doctorCalls = 0;

        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalls++;
                return new DoctorReport(1, DoctorReport.NotFound, []);
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("failed", report.State);
        Assert.Equal("gameInstallationNotFound", Assert.Single(report.Problems).Code);
        Assert.Equal(1, doctorCalls);
        Assert.False(Directory.Exists(Path.Combine(source.Path, ".sdvkit")));
        Assert.True(Directory.Exists(Path.Combine(lab.Path, ".sdvkit", "lab", "single")));
    }

    [Fact]
    public void SingleSmokeBlocksForRetainedNetworkRoleStateBeforeDoctor()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("Example.csproj", MinimalProject());
        source.WriteFile("manifest.json", CodeManifest());
        LiveLabPaths singlePaths = LiveLabPaths.Resolve(lab.Path);
        LiveLabPaths farmhandPaths = LiveLabPaths.ResolveNetworkRole(
            singlePaths,
            NetworkTwoContract.FarmhandRole);
        farmhandPaths.EnsureDirectories();
        var network = new NetworkTwoLaunchState(
            NetworkTwoContract.FarmhandRole,
            $"sha256:{new string('a', 64)}",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "SDVKit_123456789",
            Path.Combine(farmhandPaths.RuntimePath, "network-2.log"),
            ExpectedFarmhandId: 202L);
        var retained = new LiveLabState(
            LiveLabState.CurrentSchemaVersion,
            NetworkTwoContract.Topology,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            new OwnedProcessIdentity(
                4242,
                new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
                Path.Combine(lab.Path, "StardewModdingAPI.exe")),
            farmhandPaths.ModsPath,
            farmhandPaths.StatusPath,
            farmhandPaths.StopRequestPath,
            NetworkTwo: network);
        new JsonLiveLabStateStore(farmhandPaths.StatePath).Write(retained);
        var doctorCalled = false;

        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Doctor should not run.");
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("blocked", report.State);
        Assert.Equal("labNotStopped", Assert.Single(report.Problems).Code);
        Assert.False(doctorCalled);
    }

    [Theory]
    [InlineData(0, "projectFileNotFound")]
    [InlineData(2, "projectFileAmbiguous")]
    public void ProjectSmokeRequiresExactlyOneCSharpProject(
        int projectCount,
        string expectedProblem)
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("manifest.json", CodeManifest());
        for (var index = 0; index < projectCount; index++)
        {
            source.WriteFile($"Example{index}.csproj", MinimalProject());
        }

        var doctorCalled = false;
        (int exitCode, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () =>
            {
                doctorCalled = true;
                throw new InvalidOperationException("Doctor should not run.");
            });

        Assert.Equal(3, exitCode);
        Assert.Equal("unsupported", report.State);
        Assert.Equal(expectedProblem, Assert.Single(report.Problems).Code);
        Assert.False(doctorCalled);
        AssertNoProjectState(source, lab);
    }

    [Fact]
    public void EarlyOutcomeHasTheExactJsonShapeAndProofWarnings()
    {
        using TemporaryDirectory source = new();
        using TemporaryDirectory lab = new();
        source.WriteFile("manifest.json", ContentPackManifest());

        (_, ProjectSmokeReport report) = Execute(
            source.Path,
            lab.Path,
            () => throw new InvalidOperationException("Doctor should not run."));

        string json = JsonSerializer.Serialize(report, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            [
                "schemaVersion",
                "root",
                "labRoot",
                "topology",
                "state",
                "artifact",
                "roles",
                "fixtureReset",
                "stagingRemoved",
                "loadErrors",
                "problems",
                "warnings",
            ],
            PropertyNames(root));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("artifact").ValueKind);
        Assert.Empty(root.GetProperty("roles").EnumerateArray());
        Assert.False(root.GetProperty("fixtureReset").GetBoolean());
        Assert.True(root.GetProperty("stagingRemoved").GetBoolean());
        Assert.Empty(root.GetProperty("loadErrors").EnumerateArray());
        Assert.Equal(
            ["code", "path", "message"],
            PropertyNames(root.GetProperty("problems").EnumerateArray().Single()));
        Assert.Equal(
            [
                "The build identity hashes the controlled staged package file set; it is echoed by the game-side marker, not measured from the runtime DLL in memory.",
                "A passed project smoke proves that SMAPI loaded the expected UniqueID and version and completed the bounded 120-tick smoke; it does not prove that every mod feature is functionally correct.",
                "Only the isolated SMAPI mod group and exact SDVKit disposable fixture are controlled. Stardew AppData preferences and standard SMAPI logs remain shared; personal saves and the normal Mods directory are never selected or modified.",
            ],
            report.Warnings);
    }

    [Fact]
    public void PassedArtifactAndRoleHaveTheExactJsonContractShape()
    {
        var report = new ProjectSmokeReport(
            1,
            "C:/source",
            "C:/lab",
            "single",
            "passed",
            new ProjectSmokeArtifactReport(
                "Nana.Example",
                "1.0.0",
                "1.0",
                ".sdvkit/packages/Example.zip",
                ["Example/manifest.json", "Example/Example.dll"],
                $"sha256:{new string('a', 64)}",
                $"sha256:{new string('b', 64)}",
                ".sdvkit/logs/build.log",
                ".sdvkit/logs/package.log"),
            [
                new ProjectSmokeRoleReport(
                    "single",
                    "passed",
                    ".sdvkit/lab/single/mods/Example",
                    $"sha256:{new string('b', 64)}",
                    LoadConfirmed: true,
                    "Nana.Example",
                    "1.0.0",
                    120,
                    120,
                    [".sdvkit/lab/single/test-save/logs/scenario.log"]),
            ],
            FixtureReset: true,
            StagingRemoved: true,
            [],
            [],
            []);

        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(report, JsonOptions));
        JsonElement artifact = document.RootElement.GetProperty("artifact");
        JsonElement role = document.RootElement.GetProperty("roles").EnumerateArray().Single();

        Assert.Equal(
            [
                "uniqueId",
                "version",
                "declaredVersion",
                "archive",
                "entries",
                "packageHash",
                "buildIdentity",
                "buildLog",
                "packageLog",
            ],
            PropertyNames(artifact));
        Assert.Equal(
            [
                "role",
                "state",
                "stagingPath",
                "stagedBuildIdentity",
                "loadConfirmed",
                "loadedUniqueId",
                "loadedVersion",
                "requiredTicks",
                "observedTicks",
                "logPaths",
            ],
            PropertyNames(role));
    }

    private static (int ExitCode, ProjectSmokeReport Report) Execute(
        string sourcePath,
        string labRoot,
        Func<DoctorReport> doctor)
    {
        LiveLabCommandResult result = ProjectSmokeService.Execute(
            sourcePath,
            "single",
            labRoot,
            doctor);
        return (result.ExitCode, Assert.IsType<ProjectSmokeReport>(result.Report));
    }

    private static void AssertNoProjectState(
        TemporaryDirectory source,
        TemporaryDirectory lab)
    {
        Assert.False(Directory.Exists(Path.Combine(source.Path, ".sdvkit")));
        Assert.False(Directory.Exists(Path.Combine(lab.Path, ".sdvkit")));
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static string MinimalProject() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\" />";

    private static string CodeManifest(string dependencies = "[]") => $$"""
        {
          "Name": "Example",
          "Author": "Nana",
          "UniqueID": "Nana.Example",
          "Version": "1.0.0",
          "Description": "Example mod.",
          "EntryDll": "Example.dll",
          "Dependencies": {{dependencies}}
        }
        """;

    private static string ContentPackManifest(string uniqueId = "Nana.ExamplePack") => $$"""
        {
          "Name": "Example pack",
          "Author": "Nana",
          "UniqueID": "{{uniqueId}}",
          "Version": "1.0.0",
          "Description": "Example content pack.",
          "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher" }
        }
        """;
}

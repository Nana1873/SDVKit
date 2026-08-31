using System.Text.Json;
using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public void NoArgumentsPrintsTheSmallPublicSurface()
    {
        (int exitCode, string output, string error) = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("modding toolkit", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live test lab", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sdvkit version", output, StringComparison.Ordinal);
        Assert.Contains("sdvkit doctor --json", output, StringComparison.Ordinal);
        Assert.Contains("sdvkit project inspect [path] --json", output, StringComparison.Ordinal);
        Assert.Contains("sdvkit project create", output, StringComparison.Ordinal);
        Assert.Contains("sdvkit project build [path] --json", output, StringComparison.Ordinal);
        Assert.Contains("sdvkit project package [path] --json", output, StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project smoke [path] --topology <single|network-2> --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit lab <start|status|stop|test-save> --topology single --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit lab smoke --topology network-2 --json",
            output,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TextVersionIsAvailable()
    {
        (int exitCode, string output, string error) = Run("--version");

        Assert.Equal(0, exitCode);
        Assert.StartsWith("SDVKit 0.1.0", output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void JsonVersionHasOnlyThePublicFields()
    {
        (int exitCode, string output, string error) = Run("version", "--json");

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal("sdvkit", root.GetProperty("name").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void DoctorWritesStableJsonForAReadyInstallation()
    {
        using TemporaryDirectory temporary = new();
        temporary.CreateReadyInstallation();
        DoctorReport report = GameInstallationDiscovery.Inspect([temporary.Path]);

        (int exitCode, string output, string error) = RunWithDoctor(
            () => report,
            "doctor",
            "--json");

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        JsonElement installation = root.GetProperty("installations").EnumerateArray().Single();
        Assert.Equal(temporary.Path, installation.GetProperty("gamePath").GetString());
        Assert.Equal(["schemaVersion", "status", "installations"], PropertyNames(root));
        Assert.Equal(["gamePath"], PropertyNames(installation));
        Assert.Equal(string.Empty, error);
        Assert.False(Directory.Exists(System.IO.Path.Combine(temporary.Path, ".sdvkit")));
    }

    [Fact]
    public void DoctorOutcomeUsesExitThreeAndKeepsStderrEmpty()
    {
        DoctorReport report = GameInstallationDiscovery.Inspect([]);

        (int exitCode, string output, string error) = RunWithDoctor(
            () => report,
            "doctor",
            "--json");

        Assert.Equal(3, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("notFound", document.RootElement.GetProperty("status").GetString());
        Assert.Empty(document.RootElement.GetProperty("installations").EnumerateArray());
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("doctor", "--json", "extra")]
    [InlineData("doctor", "--pretty")]
    public void DoctorSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = RunWithDoctor(
            () => throw new InvalidOperationException("Discovery should not run."),
            arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal($"Usage: sdvkit doctor --json{Environment.NewLine}", error);
    }

    [Fact]
    public void ProjectInspectWritesStableJson()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        temporary.WriteFile("manifest.json", """
            {
              "Name": "Example",
              "Author": "Nana",
              "UniqueID": "Nana.Example",
              "Version": "1.0.0",
              "Description": "Example mod.",
              "EntryDll": "Example.dll"
            }
            """);

        (int exitCode, string output, string error) = Run(
            "project",
            "inspect",
            temporary.Path,
            "--json");

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal("smapiMod", root.GetProperty("kind").GetString());
        Assert.Equal(temporary.Path, root.GetProperty("root").GetString());
        Assert.Equal(["schemaVersion", "root", "kind", "projectFiles", "manifests", "problems"], PropertyNames(root));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectInspectionOutcomeUsesExitThreeAndJsonProblems()
    {
        using TemporaryDirectory temporary = new();

        (int exitCode, string output, string error) = Run(
            "project",
            "inspect",
            "--json",
            temporary.Path);

        Assert.Equal(3, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement problem = document.RootElement.GetProperty("problems").EnumerateArray().Single();
        Assert.Equal("manifestNotFound", problem.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("path").ValueKind);
        Assert.Equal(["code", "path"], PropertyNames(problem));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectCreateWritesSmallStableJson()
    {
        using TemporaryDirectory temporary = new();
        string target = System.IO.Path.Combine(temporary.Path, "ExamplePack");

        (int exitCode, string output, string error) = Run(
            "project",
            "create",
            "content-pack",
            target,
            "--name",
            "Example pack",
            "--author",
            "Nana",
            "--unique-id",
            "Nana.ExamplePack",
            "--description",
            "A minimal example.",
            "--json");

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(target, root.GetProperty("root").GetString());
        Assert.Equal("contentPack", root.GetProperty("kind").GetString());
        Assert.Equal(
            [".gitignore", "content.json", "manifest.json"],
            root.GetProperty("files").EnumerateArray().Select(value => value.GetString()));
        Assert.Empty(root.GetProperty("problems").EnumerateArray());
        Assert.Equal(
            ["schemaVersion", "root", "kind", "files", "problems"],
            PropertyNames(root));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ContentPackBuildIsAControlledJsonOutcome()
    {
        using TemporaryDirectory temporary = new();
        temporary.WriteFile("manifest.json", """
            {
              "Name": "Pack",
              "Author": "Nana",
              "UniqueID": "Nana.Pack",
              "Version": "1.0.0",
              "Description": "Example.",
              "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher" }
            }
            """);
        temporary.WriteFile("content.json", "{ \"Format\": \"2.9.0\", \"Changes\": [] }");

        (int exitCode, string output, string error) = RunWithDoctor(
            () => throw new InvalidOperationException("Discovery should not run."),
            "project",
            "build",
            temporary.Path,
            "--json");

        Assert.Equal(3, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(
            "projectNotBuildable",
            document.RootElement
                .GetProperty("problems")
                .EnumerateArray()
                .Single()
                .GetProperty("code")
                .GetString());
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void GeneratedContentPackPackagesThroughTheCli()
    {
        using TemporaryDirectory temporary = new();
        string target = System.IO.Path.Combine(temporary.Path, "Pack");
        ProjectCreator.Create(new ProjectCreationRequest(
            ProjectCreator.ContentPack,
            target,
            "Pack",
            "Nana",
            "Nana.Pack",
            "Example."));

        (int exitCode, string output, string error) = RunWithDoctor(
            () => throw new InvalidOperationException("Discovery should not run."),
            "project",
            "package",
            "--json",
            target);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(".sdvkit/packages/Pack 1.0.0.zip", root.GetProperty("archive").GetString());
        Assert.Equal(
            ["Pack/content.json", "Pack/manifest.json"],
            root.GetProperty("entries").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["schemaVersion", "root", "kind", "archive", "entries", "log", "problems"],
            PropertyNames(root));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("project", "inspect")]
    [InlineData("project", "inspect", "one", "two", "--json")]
    [InlineData("project", "inspect", "--unknown", "--json")]
    public void ProjectSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = Run(arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal($"Usage: sdvkit project inspect [path] --json{Environment.NewLine}", error);
    }

    [Theory]
    [InlineData("project", "create", "smapi-mod", "target", "--json")]
    [InlineData("project", "create", "unknown", "target", "--json")]
    [InlineData("project", "create", "content-pack", "target", "--name", "Pack", "--name", "Again", "--json")]
    public void ProjectCreateSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = Run(arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            "Usage: sdvkit project create <smapi-mod|content-pack> <path> --name <name> --author <author> --unique-id <id> --description <text> --json"
                + Environment.NewLine,
            error);
    }

    [Theory]
    [InlineData("project", "build")]
    [InlineData("project", "build", "one", "two", "--json")]
    public void ProjectBuildSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = Run(arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal($"Usage: sdvkit project build [path] --json{Environment.NewLine}", error);
    }

    [Theory]
    [InlineData("project", "package")]
    [InlineData("project", "package", "--pretty")]
    public void ProjectPackageSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = Run(arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal($"Usage: sdvkit project package [path] --json{Environment.NewLine}", error);
    }

    [Fact]
    public void ProjectHelpListsTheExactSmokeCommand()
    {
        (int exitCode, string output, string error) = Run("project", "--help");

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "Usage: sdvkit project smoke [path] --topology <single|network-2> --json",
            output,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void ProjectSmokeHelpUsesTheExactUsage(string help)
    {
        ProjectSmokeCommandRunner runner = (_, _, _) =>
            throw new InvalidOperationException("Project smoke should not run.");

        (int exitCode, string output, string error) = RunWithProjectSmoke(
            runner,
            "project",
            "smoke",
            help);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "Usage: sdvkit project smoke [path] --topology <single|network-2> --json"
                + Environment.NewLine,
            output);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("network-2")]
    public void ProjectSmokeDispatchesAnExplicitPathAndWritesStableJson(string topology)
    {
        string sourcePath = Path.Combine(Environment.CurrentDirectory, "ExampleMod");
        string? receivedSourcePath = null;
        string? receivedTopology = null;
        string? receivedLabRoot = null;
        ProjectSmokeCommandRunner runner = (candidateSourcePath, candidateTopology, labRoot) =>
        {
            receivedSourcePath = candidateSourcePath;
            receivedTopology = candidateTopology;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(0, new
            {
                schemaVersion = 1,
                root = candidateSourcePath,
                topology = candidateTopology,
                state = "passed",
                problems = Array.Empty<object>(),
            });
        };

        (int exitCode, string output, string error) = RunWithProjectSmoke(
            runner,
            "project",
            "smoke",
            "--json",
            sourcePath,
            "--topology",
            topology);

        Assert.Equal(0, exitCode);
        Assert.Equal(sourcePath, receivedSourcePath);
        Assert.Equal(topology, receivedTopology);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(sourcePath, root.GetProperty("root").GetString());
        Assert.Equal(topology, root.GetProperty("topology").GetString());
        Assert.Equal("passed", root.GetProperty("state").GetString());
        Assert.Equal(
            ["schemaVersion", "root", "topology", "state", "problems"],
            PropertyNames(root));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectSmokeDefaultsTheSourceAndLabRootsToTheCurrentDirectory()
    {
        string? receivedSourcePath = null;
        string? receivedLabRoot = null;
        ProjectSmokeCommandRunner runner = (sourcePath, topology, labRoot) =>
        {
            receivedSourcePath = sourcePath;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(0, new
            {
                schemaVersion = 1,
                topology,
                state = "passed",
            });
        };

        (int exitCode, string output, string error) = RunWithProjectSmoke(
            runner,
            "project",
            "smoke",
            "--topology",
            "single",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(Environment.CurrentDirectory, receivedSourcePath);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        Assert.NotEqual(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectSmokePropagatesAControlledJsonOutcome()
    {
        ProjectSmokeCommandRunner runner = (sourcePath, topology, _) =>
            new(3, new
            {
                schemaVersion = 1,
                root = sourcePath,
                topology,
                state = "failed",
                problems = new[]
                {
                    new
                    {
                        code = "unsupportedProject",
                        path = "manifest.json",
                        message = "Only one standalone SMAPI code mod is supported.",
                    },
                },
            });

        (int exitCode, string output, string error) = RunWithProjectSmoke(
            runner,
            "project",
            "smoke",
            "--topology",
            "network-2",
            "--json");

        Assert.Equal(3, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal("failed", root.GetProperty("state").GetString());
        JsonElement problem = root.GetProperty("problems").EnumerateArray().Single();
        Assert.Equal("unsupportedProject", problem.GetProperty("code").GetString());
        Assert.Equal(
            ["code", "path", "message"],
            PropertyNames(problem));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("project", "smoke")]
    [InlineData("project", "smoke", "--json")]
    [InlineData("project", "smoke", "--topology", "single")]
    [InlineData("project", "smoke", "--topology", "single", "--json", "--json")]
    [InlineData("project", "smoke", "--topology", "single", "--topology", "network-2", "--json")]
    [InlineData("project", "smoke", "--topology", "--json")]
    [InlineData("project", "smoke", "--topology", "local", "--json")]
    [InlineData("project", "smoke", "--topology", "Single", "--json")]
    [InlineData("project", "smoke", "one", "two", "--topology", "single", "--json")]
    [InlineData("project", "smoke", "--unknown", "--topology", "single", "--json")]
    [InlineData("project", "smoke", "--topology", "network-2", "--pretty")]
    public void ProjectSmokeSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        ProjectSmokeCommandRunner runner = (_, _, _) =>
            throw new InvalidOperationException("Project smoke should not run.");

        (int exitCode, string output, string error) = RunWithProjectSmoke(
            runner,
            arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            "Usage: sdvkit project smoke [path] --topology <single|network-2> --json"
                + Environment.NewLine,
            error);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("project", "list", "--json")]
    public void MissingOrUnknownProjectCommandReturnsProjectUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = Run(arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            "Usage: sdvkit project <inspect|create|build|package|smoke> ..."
                + Environment.NewLine,
            error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("test-save")]
    public void LabDispatchesOnlyTheSingleTopology(string action)
    {
        string? receivedAction = null;
        string? receivedTopology = null;
        string? receivedRoot = null;
        LiveLabCommandRunner runner = (candidateAction, topology, projectRoot) =>
        {
            receivedAction = candidateAction;
            receivedTopology = topology;
            receivedRoot = projectRoot;
            return new LiveLabCommandResult(0, new
            {
                schemaVersion = 1,
                topology = "single",
                state = "test",
            });
        };

        (int exitCode, string output, string error) = RunWithLab(
            runner,
            "lab",
            action,
            "--json",
            "--topology",
            "single");

        Assert.Equal(0, exitCode);
        Assert.Equal(action, receivedAction);
        Assert.Equal("single", receivedTopology);
        Assert.Equal(Environment.CurrentDirectory, receivedRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("single", document.RootElement.GetProperty("topology").GetString());
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void LabHelpListsTheSingleCommandsAndExactNetworkTwoSmoke()
    {
        (int exitCode, string output, string error) = Run("lab", "--help");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "Usage: sdvkit lab <start|status|stop|test-save> --topology single --json"
                + Environment.NewLine
                + "       sdvkit lab smoke --topology network-2 --json"
                + Environment.NewLine,
            output);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void LabDispatchesTheExactNetworkTwoSmoke()
    {
        string? receivedAction = null;
        string? receivedTopology = null;
        string? receivedRoot = null;
        LiveLabCommandRunner runner = (candidateAction, topology, projectRoot) =>
        {
            receivedAction = candidateAction;
            receivedTopology = topology;
            receivedRoot = projectRoot;
            return new LiveLabCommandResult(0, new
            {
                schemaVersion = 1,
                topology = "network-2",
                state = "passed",
            });
        };

        (int exitCode, string output, string error) = RunWithLab(
            runner,
            "lab",
            "smoke",
            "--topology",
            "network-2",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal("smoke", receivedAction);
        Assert.Equal("network-2", receivedTopology);
        Assert.Equal(Environment.CurrentDirectory, receivedRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("network-2", document.RootElement.GetProperty("topology").GetString());
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("lab")]
    [InlineData("lab", "start", "--json")]
    [InlineData("lab", "start", "--topology", "network-2", "--json")]
    [InlineData("lab", "status", "--topology", "network-2", "--json")]
    [InlineData("lab", "smoke", "--topology", "single", "--json")]
    [InlineData("lab", "start", "--topology", "single", "--pretty")]
    [InlineData("lab", "up", "--topology", "single", "--json")]
    [InlineData("lab", "test-save", "--topology", "single", "--json", "--fixture", "other")]
    public void LabSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        LiveLabCommandRunner runner = (_, _, _) =>
            throw new InvalidOperationException("Lab command should not run.");

        (int exitCode, string output, string error) = RunWithLab(runner, arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            "Usage: sdvkit lab <start|status|stop|test-save> --topology single --json"
                + Environment.NewLine
                + "       sdvkit lab smoke --topology network-2 --json"
                + Environment.NewLine,
            error);
    }

    [Fact]
    public void UnknownCommandReturnsUsageError()
    {
        (int exitCode, string output, string error) = Run("save-everything");

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Unknown command", error, StringComparison.Ordinal);
    }

    private static string[] PropertyNames(JsonElement element)
    {
        return element.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        return RunWithDoctor(GameInstallationDiscovery.Discover, arguments);
    }

    private static (int ExitCode, string Output, string Error) RunWithDoctor(
        Func<DoctorReport> discoverInstallations,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(arguments, output, error, discoverInstallations);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunWithLab(
        LiveLabCommandRunner runLiveLab,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runLiveLab);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunWithProjectSmoke(
        ProjectSmokeCommandRunner runProjectSmoke,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectSmoke: runProjectSmoke);
        return (exitCode, output.ToString(), error.ToString());
    }
}

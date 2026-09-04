using System.Globalization;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

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
            "sdvkit project review start [code-project-or-content-pack] [--topology <single|network-2>] [--test-save] [--companion <path>]... [--content-pack <path>]... --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review map <assets|get|layers|layer|tilesheets|warps|tile|property> ... [--topology single] --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "Owned review-fixture console lines are transported as <text>",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review status [--topology <single|network-2>] --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review stop [--topology <single|network-2>] --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review reset --topology <single|network-2> --json",
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
        Assert.StartsWith("SDVKit 0.6.1", output, StringComparison.Ordinal);
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
        Assert.Equal("0.6.1", root.GetProperty("version").GetString());
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
        Assert.Contains(
            "Usage: sdvkit project review start [code-project-or-content-pack] [--topology <single|network-2>] [--test-save] [--companion <path>]... [--content-pack <path>]... --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review reset --topology <single|network-2> --json",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit fixture building ensure <alias> <building-kind> <x> <y>",
            output,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectReviewStartDispatchesOnlyTheExplicitOrderedSources()
    {
        string target = Path.Combine(Environment.CurrentDirectory, "Target");
        string companionOne = Path.Combine(Environment.CurrentDirectory, "CompanionOne");
        string companionTwo = Path.Combine(Environment.CurrentDirectory, "ReadyMod");
        string contentPack = Path.Combine(Environment.CurrentDirectory, "Pack");
        string? receivedAction = null;
        string? receivedTarget = null;
        IReadOnlyList<string>? receivedCompanions = null;
        IReadOnlyList<string>? receivedPacks = null;
        string? receivedTopology = null;
        string? receivedLabRoot = null;
        ProjectReviewCommandRunner runner = (
            action,
            sourcePath,
            companionPaths,
            contentPackPaths,
            useTestSave,
            topology,
            labRoot) =>
        {
            receivedAction = action;
            receivedTarget = sourcePath;
            receivedCompanions = companionPaths;
            receivedPacks = contentPackPaths;
            Assert.False(useTestSave);
            receivedTopology = topology;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(0, new
            {
                schemaVersion = 1,
                state = "running",
            });
        };

        (int exitCode, string output, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            "start",
            "--topology",
            "network-2",
            "--companion",
            companionOne,
            target,
            "--content-pack",
            contentPack,
            "--companion",
            companionTwo,
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal("start", receivedAction);
        Assert.Equal(target, receivedTarget);
        Assert.Equal([companionOne, companionTwo], receivedCompanions);
        Assert.Equal([contentPack], receivedPacks);
        Assert.Equal("network-2", receivedTopology);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        Assert.Equal("running", JsonDocument.Parse(output).RootElement
            .GetProperty("state").GetString());
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectReviewStartDefaultsTargetAndLabRootToCurrentDirectory()
    {
        string? receivedTarget = null;
        ProjectReviewCommandRunner runner = (
            _,
            sourcePath,
            companionPaths,
            contentPackPaths,
            useTestSave,
            topology,
            labRoot) =>
        {
            receivedTarget = sourcePath;
            Assert.Empty(companionPaths);
            Assert.Empty(contentPackPaths);
            Assert.False(useTestSave);
            Assert.Equal("single", topology);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return new LiveLabCommandResult(0, new { state = "running" });
        };

        (int exitCode, _, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            "start",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(Environment.CurrentDirectory, receivedTarget);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectReviewStartDispatchesTheExplicitSingleTestSaveSelection()
    {
        bool? receivedUseTestSave = null;
        ProjectReviewCommandRunner runner = (
            _,
            _,
            _,
            _,
            useTestSave,
            topology,
            _) =>
        {
            receivedUseTestSave = useTestSave;
            Assert.Equal("single", topology);
            return new LiveLabCommandResult(0, new { state = "running" });
        };

        (int exitCode, _, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            "start",
            "--topology",
            "single",
            "--test-save",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.True(receivedUseTestSave);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public void ProjectReviewLifecycleCommandsUseTheCurrentLabOnly(string action)
    {
        ProjectReviewCommandRunner runner = (
            receivedAction,
            sourcePath,
            companionPaths,
            contentPackPaths,
            useTestSave,
            topology,
            labRoot) =>
        {
            Assert.Equal(action, receivedAction);
            Assert.Equal(Environment.CurrentDirectory, sourcePath);
            Assert.Empty(companionPaths);
            Assert.Empty(contentPackPaths);
            Assert.False(useTestSave);
            Assert.Equal("single", topology);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return new LiveLabCommandResult(0, new { state = "stopped" });
        };

        (int exitCode, _, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            action,
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public void ProjectReviewLifecycleCommandsCanAddressNetworkTwo(string action)
    {
        ProjectReviewCommandRunner runner = (
            receivedAction,
            sourcePath,
            companionPaths,
            contentPackPaths,
            useTestSave,
            topology,
            labRoot) =>
        {
            Assert.Equal(action, receivedAction);
            Assert.Equal(Environment.CurrentDirectory, sourcePath);
            Assert.Empty(companionPaths);
            Assert.Empty(contentPackPaths);
            Assert.False(useTestSave);
            Assert.Equal("network-2", topology);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return new LiveLabCommandResult(0, new { state = "ready" });
        };

        (int exitCode, _, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            action,
            "--topology",
            "network-2",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectReviewCommandDispatchesOneExactLineToTheCurrentLabOnly()
    {
        const string command = "sdvkit fixture status";
        string? receivedCommand = null;
        string? receivedTopology = null;
        string? receivedRole = "unexpected";
        string? receivedLabRoot = null;
        ProjectReviewCommandRunner reviewRunner = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Review lifecycle should not run.");
        ProjectReviewConsoleCommandRunner consoleRunner = (
            candidate,
            topology,
            role,
            labRoot) =>
        {
            receivedCommand = candidate;
            receivedTopology = topology;
            receivedRole = role;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(0, new
            {
                schemaVersion = 1,
                state = "running",
                commandWritten = true,
            });
        };

        (int exitCode, string output, string error) = RunWithProjectReview(
            reviewRunner,
            consoleRunner,
            "project",
            "review",
            "command",
            command,
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(command, receivedCommand);
        Assert.Equal("single", receivedTopology);
        Assert.Null(receivedRole);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        Assert.True(JsonDocument.Parse(output).RootElement
            .GetProperty("commandWritten").GetBoolean());
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("farmhand")]
    public void ProjectReviewNetworkCommandDispatchesOneExactRole(string expectedRole)
    {
        const string command = "sdvkit fixture status";
        ProjectReviewCommandRunner reviewRunner = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Review lifecycle should not run.");
        ProjectReviewConsoleCommandRunner consoleRunner = (
            candidate,
            topology,
            role,
            labRoot) =>
        {
            Assert.Equal(command, candidate);
            Assert.Equal("network-2", topology);
            Assert.Equal(expectedRole, role);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return new LiveLabCommandResult(0, new { commandWritten = true });
        };

        (int exitCode, _, string error) = RunWithProjectReview(
            reviewRunner,
            consoleRunner,
            "project",
            "review",
            "command",
            command,
            "--topology",
            "network-2",
            "--role",
            expectedRole,
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("network-2")]
    public void ProjectReviewResetDispatchesOnlyTheExplicitTopology(string expectedTopology)
    {
        ProjectReviewCommandRunner runner = (
            action,
            sourcePath,
            companionPaths,
            contentPackPaths,
            useTestSave,
            topology,
            labRoot) =>
        {
            Assert.Equal("reset", action);
            Assert.Equal(Environment.CurrentDirectory, sourcePath);
            Assert.Empty(companionPaths);
            Assert.Empty(contentPackPaths);
            Assert.False(useTestSave);
            Assert.Equal(expectedTopology, topology);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return new LiveLabCommandResult(0, new { state = "reset" });
        };

        (int exitCode, _, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            "reset",
            "--topology",
            expectedTopology,
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ProjectReviewDataDispatchesBoundedSingleQueriesAndWritesStableJson()
    {
        ReviewDataQuery? received = null;
        string? receivedLabRoot = null;
        ProjectReviewDataCommandRunner runner = (query, labRoot) =>
        {
            received = query;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(
                0,
                new ReviewDataReport(
                    ReviewDataContract.SchemaVersion,
                    "ready",
                    query.Operation,
                    "1.6.15",
                    "1.6.15.24356",
                    "Data/Buildings",
                    "Dictionary",
                    "dictionary",
                    "string",
                    null,
                    null,
                    ["Barn", "Coop"],
                    new ReviewDataPage(5, 2, 2, 12, 7),
                    null,
                    null,
                    []));
        };

        (int exitCode, string output, string error) = RunWithProjectReviewData(
            runner,
            "project",
            "review",
            "data",
            "keys",
            "Data/Buildings",
            "--offset",
            "5",
            "--limit",
            "2",
            "--topology",
            "single",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            new ReviewDataQuery(
                ReviewDataContract.KeysOperation,
                "Data/Buildings",
                null,
                5,
                2),
            received);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal("1.6.15.24356", root.GetProperty("gameFileVersion").GetString());
        Assert.Equal("Data/Buildings", root.GetProperty("assetName").GetString());
        Assert.Equal("string", root.GetProperty("keyKind").GetString());
        Assert.Equal(["Barn", "Coop"], root
            .GetProperty("keys")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray());
    }

    [Theory]
    [InlineData("project", "review", "data")]
    [InlineData("project", "review", "data", "unknown", "--json")]
    [InlineData("project", "review", "data", "assets")]
    [InlineData("project", "review", "data", "assets", "extra", "--json")]
    [InlineData("project", "review", "data", "assets", "--limit", "0", "--json")]
    [InlineData("project", "review", "data", "assets", "--limit", "101", "--json")]
    [InlineData("project", "review", "data", "assets", "--offset", "-1", "--json")]
    [InlineData("project", "review", "data", "assets", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "data", "keys", "--json")]
    [InlineData("project", "review", "data", "get", "Data/Buildings", "--json")]
    [InlineData("project", "review", "data", "get", "Data/Buildings", "Barn", "--limit", "1", "--json")]
    [InlineData("project", "review", "data", "get", "Data/Buildings", "Barn", "--json", "--json")]
    public void ProjectReviewDataSyntaxErrorsUseTheExactDataUsage(params string[] arguments)
    {
        ProjectReviewDataCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-data should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewData(
            runner,
            arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains(
            "sdvkit project review data assets",
            error,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review data keys <asset>",
            error,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review data get <asset> <key>",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data", "--help")]
    [InlineData("data", "assets", "--help")]
    public void ProjectReviewDataHelpListsOnlyTheBoundedSingleSurface(
        params string[] suffix)
    {
        ProjectReviewDataCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-data should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewData(
            runner,
            ["project", "review", .. suffix]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("data assets", output, StringComparison.Ordinal);
        Assert.Contains("data keys <asset>", output, StringComparison.Ordinal);
        Assert.Contains("data get <asset> <key>", output, StringComparison.Ordinal);
        Assert.Contains("active owned single review", output, StringComparison.Ordinal);
        Assert.DoesNotContain("network-2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReviewMapDispatchesPagedInventoryQuery()
    {
        ReviewMapQuery? received = null;
        ProjectReviewMapCommandRunner runner = (query, labRoot) =>
        {
            received = query;
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return new LiveLabCommandResult(0, new { state = "ready", operation = query.Operation });
        };

        (int exitCode, string output, string error) = RunWithProjectReviewMap(
            runner,
            "project",
            "review",
            "map",
            "assets",
            "--offset",
            "5",
            "--limit",
            "2",
            "--topology",
            "single",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            new ReviewMapQuery("assets", null, null, null, null, null, null, null, null, 5, 2),
            received);
        Assert.Contains("\"operation\":\"assets\"", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("map", "direct", null)]
    [InlineData("layer", "direct", null)]
    [InlineData("tile", "direct", null)]
    [InlineData("tile", "tile-index", 1)]
    public void ProjectReviewMapDispatchesExplicitPropertyScopes(
        string scope,
        string source,
        int? frame)
    {
        ReviewMapQuery? received = null;
        ProjectReviewMapCommandRunner runner = (query, _) =>
        {
            received = query;
            return new LiveLabCommandResult(0, new { state = "ready" });
        };
        var arguments = new List<string> { "project", "review", "map", "property", "Maps/Town", scope };
        if (scope == "map")
        {
            arguments.Add("Outdoors");
        }
        else if (scope == "layer")
        {
            arguments.AddRange(["Buildings", "NoSpawn"]);
        }
        else
        {
            arguments.AddRange(["Buildings", "1", "2", source, "Action"]);
        }
        if (frame is not null)
        {
            arguments.AddRange(["--frame", frame.Value.ToString(CultureInfo.InvariantCulture)]);
        }
        arguments.Add("--json");

        (int exitCode, _, string error) = RunWithProjectReviewMap(runner, [.. arguments]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(scope, received!.PropertyScope);
        Assert.Equal(source, received.PropertySource);
        Assert.Equal(frame, received.FrameIndex);
    }

    [Fact]
    public void ProjectReviewMapEndOfOptionsAllowsAnOptionLikeLayerId()
    {
        ReviewMapQuery? received = null;
        ProjectReviewMapCommandRunner runner = (query, _) =>
        {
            received = query;
            return new LiveLabCommandResult(0, new { state = "ready" });
        };

        (int exitCode, _, string error) = RunWithProjectReviewMap(
            runner,
            "project",
            "review",
            "map",
            "layer",
            "Maps/Town",
            "--json",
            "--",
            "--limit");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal("Maps/Town", received!.Asset);
        Assert.Equal("--limit", received.Layer);
    }

    [Fact]
    public void ProjectReviewMapEndOfOptionsAllowsOptionLikePropertyOperands()
    {
        ReviewMapQuery? received = null;
        ProjectReviewMapCommandRunner runner = (query, _) =>
        {
            received = query;
            return new LiveLabCommandResult(0, new { state = "ready" });
        };

        (int exitCode, _, string error) = RunWithProjectReviewMap(
            runner,
            "project",
            "review",
            "map",
            "property",
            "Maps/Town",
            "layer",
            "--topology",
            "single",
            "--json",
            "--",
            "--frame",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal("Maps/Town", received!.Asset);
        Assert.Equal("layer", received.PropertyScope);
        Assert.Equal("--frame", received.Layer);
        Assert.Equal("--json", received.Property);
        Assert.Null(received.FrameIndex);
    }

    [Theory]
    [InlineData("project", "review", "map")]
    [InlineData("project", "review", "map", "unknown", "--json")]
    [InlineData("project", "review", "map", "assets")]
    [InlineData("project", "review", "map", "assets", "extra", "--json")]
    [InlineData("project", "review", "map", "assets", "--limit", "101", "--json")]
    [InlineData("project", "review", "map", "get", "Maps/Town", "--limit", "1", "--json")]
    [InlineData("project", "review", "map", "tile", "Maps/Town", "Buildings", "-1", "0", "--json")]
    [InlineData("project", "review", "map", "property", "Maps/Town", "map", "Outdoors", "--frame", "0", "--json")]
    [InlineData("project", "review", "map", "property", "Maps/Town", "tile", "Buildings", "1", "2", "direct", "Action", "--frame", "0", "--json")]
    [InlineData("project", "review", "map", "assets", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "map", "assets", "--json", "--json")]
    public void ProjectReviewMapSyntaxErrorsUseTheExactMapUsage(params string[] arguments)
    {
        ProjectReviewMapCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-map should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewMap(runner, arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("project review map assets", error, StringComparison.Ordinal);
        Assert.Contains("project review map layer <map> <layer>", error, StringComparison.Ordinal);
        Assert.Contains("project review map property", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("map", "--help")]
    [InlineData("map", "assets", "--help")]
    public void ProjectReviewMapHelpListsOnlyTheBoundedSingleSurface(params string[] suffix)
    {
        ProjectReviewMapCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-map should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewMap(
            runner,
            ["project", "review", .. suffix]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("map assets", output, StringComparison.Ordinal);
        Assert.Contains("map tile <map> <layer> <x> <y>", output, StringComparison.Ordinal);
        Assert.Contains("active owned single review", output, StringComparison.Ordinal);
        Assert.Contains("put every CLI option before '--'", output, StringComparison.Ordinal);
        Assert.Contains("every following token is treated as an operand", output, StringComparison.Ordinal);
        Assert.DoesNotContain("network-2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReviewAudioDispatchesBoundedSingleQueriesAndWritesStableJson()
    {
        ReviewAudioQuery? received = null;
        string? receivedLabRoot = null;
        ProjectReviewAudioCommandRunner runner = (query, labRoot) =>
        {
            received = query;
            receivedLabRoot = labRoot;
            return new LiveLabCommandResult(
                0,
                new ReviewAudioReport(
                    ReviewAudioContract.SchemaVersion,
                    "ready",
                    query.Operation,
                    "1.6.15",
                    "1.6.15.24356",
                    null,
                    [
                        new ReviewAudioCueReport(
                            "MainTheme",
                            [ReviewAudioContract.JukeboxTrackSource],
                            false,
                            true,
                            true,
                            3,
                            null,
                            null,
                            null,
                            null,
                            null,
                            [
                                new ReviewAudioJukeboxReference(
                                    "MainTheme",
                                    ReviewAudioContract.PrimaryJukeboxRelation),
                            ]),
                    ],
                    new ReviewAudioPage(5, 2, 1, 6, null),
                    new ReviewAudioCoverageReport(
                        0,
                        6,
                        2,
                        6,
                        1,
                        1,
                        0,
                        0,
                        true,
                        null,
                        ReviewAudioContract.BuiltInInventoryStatus),
                    []));
        };

        (int exitCode, string output, string error) = RunWithProjectReviewAudio(
            runner,
            "project",
            "review",
            "audio",
            "cues",
            "--offset",
            "5",
            "--limit",
            "2",
            "--topology",
            "single",
            "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            new ReviewAudioQuery(
                ReviewAudioContract.CuesOperation,
                null,
                5,
                2),
            received);
        Assert.Equal(Environment.CurrentDirectory, receivedLabRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal("1.6.15.24356", root.GetProperty("gameFileVersion").GetString());
        Assert.Equal(
            "MainTheme",
            root.GetProperty("cues")[0].GetProperty("cueId").GetString());
        Assert.Equal(
            "unavailableByPublicApi",
            root.GetProperty("coverage")
                .GetProperty("builtInCueInventoryStatus")
                .GetString());
    }

    [Theory]
    [InlineData("project", "review", "audio")]
    [InlineData("project", "review", "audio", "unknown", "--json")]
    [InlineData("project", "review", "audio", "cues")]
    [InlineData("project", "review", "audio", "cues", "extra", "--json")]
    [InlineData("project", "review", "audio", "cues", "--limit", "0", "--json")]
    [InlineData("project", "review", "audio", "cues", "--limit", "101", "--json")]
    [InlineData("project", "review", "audio", "cues", "--offset", "-1", "--json")]
    [InlineData("project", "review", "audio", "cues", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "audio", "cue", "--json")]
    [InlineData("project", "review", "audio", "cue", "MainTheme", "--limit", "1", "--json")]
    [InlineData("project", "review", "audio", "cue", "MainTheme", "--json", "--json")]
    [InlineData("project", "review", "audio", "cue", "--option-like", "--json")]
    public void ProjectReviewAudioSyntaxErrorsUseTheExactAudioUsage(
        params string[] arguments)
    {
        ProjectReviewAudioCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-audio should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewAudio(
            runner,
            arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains(
            "sdvkit project review audio cues",
            error,
            StringComparison.Ordinal);
        Assert.Contains(
            "sdvkit project review audio cue <id>",
            error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReviewAudioEndOfOptionsAllowsAnOptionLikeCueId()
    {
        ReviewAudioQuery? received = null;
        ProjectReviewAudioCommandRunner runner = (query, _) =>
        {
            received = query;
            return new LiveLabCommandResult(0, new { state = "ready" });
        };

        (int exitCode, _, string error) = RunWithProjectReviewAudio(
            runner,
            "project",
            "review",
            "audio",
            "cue",
            "--topology",
            "single",
            "--json",
            "--",
            "--option-like");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal("--option-like", received!.CueId);
    }

    [Fact]
    public void ProjectReviewAudioRejectsMalformedUtf16BeforeDispatch()
    {
        ProjectReviewAudioCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-audio should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewAudio(
            runner,
            "project",
            "review",
            "audio",
            "cue",
            "\ud800",
            "--json");

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("project review audio cue", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("audio", "--help")]
    [InlineData("audio", "cues", "--help")]
    public void ProjectReviewAudioHelpListsOnlyTheBoundedSingleSurface(
        params string[] suffix)
    {
        ProjectReviewAudioCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Review-audio should not run.");

        (int exitCode, string output, string error) = RunWithProjectReviewAudio(
            runner,
            ["project", "review", .. suffix]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("audio cues", output, StringComparison.Ordinal);
        Assert.Contains("audio cue <id>", output, StringComparison.Ordinal);
        Assert.Contains("active owned single review", output, StringComparison.Ordinal);
        Assert.Contains("cannot enumerate", output, StringComparison.Ordinal);
        Assert.Contains("before '--'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("network-2", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectReviewMcpServeDispatchesOnlySingleWithoutJson(bool explicitTopology)
    {
        var called = false;
        ProjectReviewMcpCommandRunner mcpRunner = (topology, role, labRoot, _) =>
        {
            called = true;
            Assert.Equal("single", topology);
            Assert.Null(role);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return 0;
        };
        string[] arguments = explicitTopology
            ? ["project", "review", "mcp", "serve", "--topology", "single"]
            : ["project", "review", "mcp", "serve"];

        (int exitCode, string output, string error) = RunWithProjectReviewMcp(
            mcpRunner,
            arguments);

        Assert.Equal(0, exitCode);
        Assert.True(called);
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(NetworkTwoContract.HostRole)]
    [InlineData(NetworkTwoContract.FarmhandRole)]
    public void ProjectReviewMcpServeDispatchesExactNetworkRole(string expectedRole)
    {
        ProjectReviewMcpCommandRunner mcpRunner = (topology, role, labRoot, _) =>
        {
            Assert.Equal(NetworkTwoContract.Topology, topology);
            Assert.Equal(expectedRole, role);
            Assert.Equal(Environment.CurrentDirectory, labRoot);
            return 0;
        };

        (int exitCode, string output, string error) = RunWithProjectReviewMcp(
            mcpRunner,
            "project",
            "review",
            "mcp",
            "serve",
            "--topology",
            "network-2",
            "--role",
            expectedRole);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("project", "review")]
    [InlineData("project", "review", "start")]
    [InlineData("project", "review", "start", "--json", "one", "two")]
    [InlineData("project", "review", "start", "--companion", "--json")]
    [InlineData("project", "review", "start", "--content-pack", "--json")]
    [InlineData("project", "review", "start", "--topology", "invalid", "--json")]
    [InlineData("project", "review", "start", "--topology", "single", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "start", "--role", "host", "--json")]
    [InlineData("project", "review", "start", "--test-save", "--test-save", "--json")]
    [InlineData("project", "review", "start", "--topology", "network-2", "--test-save", "--json")]
    [InlineData("project", "review", "status", "target", "--json")]
    [InlineData("project", "review", "status", "--companion", "mod", "--json")]
    [InlineData("project", "review", "status", "--role", "host", "--json")]
    [InlineData("project", "review", "status", "--test-save", "--json")]
    [InlineData("project", "review", "command", "--json")]
    [InlineData("project", "review", "command", "one", "two", "--json")]
    [InlineData("project", "review", "command", "one", "--json", "--json")]
    [InlineData("project", "review", "command", "--companion", "mod", "--json")]
    [InlineData("project", "review", "command", "--content-pack", "pack", "--json")]
    [InlineData("project", "review", "command", "one\ntwo", "--json")]
    [InlineData("project", "review", "command", "one", "--role", "host", "--json")]
    [InlineData("project", "review", "command", "one", "--topology", "network-2", "--json")]
    [InlineData("project", "review", "command", "one", "--topology", "network-2", "--role", "invalid", "--json")]
    [InlineData("project", "review", "command", "one", "--topology", "network-2", "--role", "host", "--role", "host", "--json")]
    [InlineData("project", "review", "stop", "--json", "--json")]
    [InlineData("project", "review", "stop", "--role", "host", "--json")]
    [InlineData("project", "review", "stop", "--test-save", "--json")]
    [InlineData("project", "review", "reset", "--json")]
    [InlineData("project", "review", "reset", "--topology", "single", "--test-save", "--json")]
    [InlineData("project", "review", "reset", "--topology", "network-2", "--role", "host", "--json")]
    [InlineData("project", "review", "reset", "--topology", "network-2", "target", "--json")]
    [InlineData("project", "review", "restart", "--json")]
    [InlineData("project", "review", "mcp")]
    [InlineData("project", "review", "mcp", "serve", "--json")]
    [InlineData("project", "review", "mcp", "serve", "--role", "host")]
    [InlineData("project", "review", "mcp", "serve", "--topology", "single", "--role", "host")]
    [InlineData("project", "review", "mcp", "serve", "--topology", "network-2")]
    [InlineData("project", "review", "mcp", "serve", "--topology", "network-2", "--role", "invalid")]
    [InlineData("project", "review", "mcp", "serve", "--topology", "network-2", "--role", "host", "--role", "host")]
    [InlineData("project", "review", "mcp", "serve", "--topology", "network-2", "--topology", "network-2", "--role", "host")]
    [InlineData("project", "review", "mcp", "serve", "--topology", "network-2", "--role")]
    public void ProjectReviewSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        ProjectReviewCommandRunner runner = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Project review should not run.");

        (int exitCode, string output, string error) = RunWithProjectReview(
            runner,
            arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            "Usage: sdvkit project review start [code-project-or-content-pack] [--topology <single|network-2>] [--test-save] [--companion <path>]... [--content-pack <path>]... --json"
                + Environment.NewLine
                + "       sdvkit project review status [--topology <single|network-2>] --json"
                + Environment.NewLine
                + "       sdvkit project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json"
                + Environment.NewLine
                + "       sdvkit project review data assets [--offset <n>] [--limit <1-100>] [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review data keys <asset> [--offset <n>] [--limit <1-100>] [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review data get <asset> <key> [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review map <assets|get|layers|layer|tilesheets|warps|tile|property> ... --json"
                + Environment.NewLine
                + "       sdvkit project review texture assets [--offset <n>] [--limit <1-100>] [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review texture get <asset> [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review texture preview <asset> [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review audio cues [--offset <n>] [--limit <1-100>] [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review audio cue <id> [--topology single] --json"
                + Environment.NewLine
                + "       sdvkit project review stop [--topology <single|network-2>] --json"
                + Environment.NewLine
                + "       sdvkit project review reset --topology <single|network-2> --json"
                + Environment.NewLine
                + "       sdvkit project review mcp serve [--topology single]"
                + Environment.NewLine
                + "       sdvkit project review mcp serve --topology network-2 --role <host|farmhand>"
                + Environment.NewLine
                + "       all MCP topologies: stardew_runtime_get, stardew_review_get, stardew_mods_list; single additionally: stardew_data_assets_list, stardew_data_keys_list, stardew_data_record_get"
                + Environment.NewLine
                + "Content-pack targets require --topology single and an explicit provider --companion."
                + Environment.NewLine
                + "AlwaysOn review console lines (quote one as <text> for project review command; not top-level CLI):"
                + Environment.NewLine
                + "  sdvkit screenshot <label>"
                + Environment.NewLine
                + "  sdvkit screenshot viewport <label>"
                + Environment.NewLine
                + "  sdvkit input press <SButton>"
                + Environment.NewLine
                + "  sdvkit input cursor <ui-x> <ui-y>"
                + Environment.NewLine
                + "  sdvkit input cursor clear"
                + Environment.NewLine
                + "  sdvkit fixture status"
                + Environment.NewLine
                + "  sdvkit fixture building ensure <alias> <building-kind> <x> <y>"
                + Environment.NewLine
                + "  sdvkit fixture object ensure <alias-or-id> <qualified-item-id>"
                + Environment.NewLine
                + "  sdvkit fixture object clear-owned <alias-or-id>"
                + Environment.NewLine
                + "  sdvkit fixture animal ensure <alias-or-id> <animal-kind>"
                + Environment.NewLine
                + "  Kinds resolve from loaded canonical Stardew data IDs; legacy deluxe-barn and white-cow remain valid."
                + Environment.NewLine
                + "  Unknown, ambiguous, unplaceable, or animal-house-incompatible kinds fail before mutation."
                + Environment.NewLine
                + "  sdvkit fixture enter <alias-or-id>"
                + Environment.NewLine
                + "  sdvkit fixture enter greenhouse"
                + Environment.NewLine
                + "  sdvkit fixture farm"
                + Environment.NewLine,
            error);
    }

    [Fact]
    public void ProjectReviewCommandRejectsALineOverTheBoundedMaximum()
    {
        ProjectReviewCommandRunner runner = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Project review should not run.");

        (int exitCode, string output, string error) = RunWithProjectReview(
            runner,
            "project",
            "review",
            "command",
            new string('x', ProjectReviewConsoleLine.MaximumLength + 1),
            "--json");

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains(
            "project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("start", "--help")]
    [InlineData("reset", "--help")]
    public void ProjectReviewHelpListsTheTopologyAddressedSurface(params string[] suffix)
    {
        ProjectReviewCommandRunner runner = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Project review should not run.");
        string[] arguments = ["project", "review", .. suffix];

        (int exitCode, string output, string error) = RunWithProjectReview(
            runner,
            arguments);

        Assert.Equal(0, exitCode);
        Assert.Contains("project review start", output, StringComparison.Ordinal);
        Assert.Contains("project review status", output, StringComparison.Ordinal);
        Assert.Contains("project review command", output, StringComparison.Ordinal);
        Assert.Contains("project review map", output, StringComparison.Ordinal);
        Assert.Contains("project review stop", output, StringComparison.Ordinal);
        Assert.Contains("project review reset", output, StringComparison.Ordinal);
        Assert.Contains("project review mcp serve", output, StringComparison.Ordinal);
        Assert.Contains("stardew_data_assets_list", output, StringComparison.Ordinal);
        Assert.Contains("stardew_review_get", output, StringComparison.Ordinal);
        Assert.Contains("stardew_mods_list", output, StringComparison.Ordinal);
        Assert.Contains("single additionally", output, StringComparison.Ordinal);
        Assert.Contains("--role <host|farmhand>", output, StringComparison.Ordinal);
        Assert.Contains(
            "Content-pack targets require --topology single and an explicit provider --companion.",
            output,
            StringComparison.Ordinal);
        string[] reviewConsoleLines =
        [
            "sdvkit screenshot <label>",
            "sdvkit screenshot viewport <label>",
            "sdvkit input press <SButton>",
            "sdvkit input cursor <ui-x> <ui-y>",
            "sdvkit input cursor clear",
            "sdvkit fixture status",
            "sdvkit fixture building ensure <alias> <building-kind> <x> <y>",
            "sdvkit fixture object ensure <alias-or-id> <qualified-item-id>",
            "sdvkit fixture object clear-owned <alias-or-id>",
            "sdvkit fixture animal ensure <alias-or-id> <animal-kind>",
            "sdvkit fixture enter <alias-or-id>",
            "sdvkit fixture enter greenhouse",
            "sdvkit fixture farm",
        ];
        foreach (string reviewConsoleLine in reviewConsoleLines)
        {
            Assert.Contains(reviewConsoleLine, output, StringComparison.Ordinal);
        }

        Assert.Contains("not top-level CLI", output, StringComparison.Ordinal);
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
            "Usage: sdvkit project <inspect|create|build|package|smoke|review> ..."
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

    [Fact]
    public void FixtureConsoleLinesAreNotATopLevelCliCommand()
    {
        (int exitCode, string output, string error) = Run("fixture", "status");

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

    private static (int ExitCode, string Output, string Error) RunWithProjectReview(
        ProjectReviewCommandRunner runProjectReview,
        params string[] arguments)
    {
        return RunWithProjectReview(
            runProjectReview,
            (_, _, _, _) => throw new InvalidOperationException(
                "Project-review console command should not run."),
            arguments);
    }

    private static (int ExitCode, string Output, string Error) RunWithProjectReview(
        ProjectReviewCommandRunner runProjectReview,
        ProjectReviewConsoleCommandRunner runProjectReviewConsole,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReview: runProjectReview,
            runProjectReviewConsole: runProjectReviewConsole);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunWithProjectReviewData(
        ProjectReviewDataCommandRunner runProjectReviewData,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReviewData: runProjectReviewData);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunWithProjectReviewMap(
        ProjectReviewMapCommandRunner runProjectReviewMap,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReviewMap: runProjectReviewMap);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunWithProjectReviewAudio(
        ProjectReviewAudioCommandRunner runProjectReviewAudio,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReviewAudio: runProjectReviewAudio);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunWithProjectReviewMcp(
        ProjectReviewMcpCommandRunner runProjectReviewMcp,
        params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = CliApplication.Run(
            arguments,
            output,
            error,
            GameInstallationDiscovery.Discover,
            runProjectReview: (_, _, _, _, _, _, _) =>
                throw new InvalidOperationException("Project review should not run."),
            runProjectReviewConsole: (_, _, _, _) =>
                throw new InvalidOperationException("Project-review console should not run."),
            runProjectReviewMcp: runProjectReviewMcp);
        return (exitCode, output.ToString(), error.ToString());
    }
}

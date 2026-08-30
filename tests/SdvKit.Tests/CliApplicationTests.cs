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
        Assert.Contains(
            "sdvkit lab <start|status|stop> --topology single --json",
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

    [Theory]
    [InlineData("project", "inspect")]
    [InlineData("project", "inspect", "one", "two", "--json")]
    [InlineData("project", "inspect", "--unknown", "--json")]
    [InlineData("project", "list", "--json")]
    public void ProjectSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        (int exitCode, string output, string error) = Run(arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal($"Usage: sdvkit project inspect [path] --json{Environment.NewLine}", error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("stop")]
    public void LabDispatchesOnlyTheSingleTopology(string action)
    {
        string? receivedAction = null;
        string? receivedRoot = null;
        LiveLabCommandRunner runner = (candidateAction, projectRoot) =>
        {
            receivedAction = candidateAction;
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
        Assert.Equal(Environment.CurrentDirectory, receivedRoot);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("single", document.RootElement.GetProperty("topology").GetString());
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("lab")]
    [InlineData("lab", "start", "--json")]
    [InlineData("lab", "start", "--topology", "network-2", "--json")]
    [InlineData("lab", "start", "--topology", "single", "--pretty")]
    [InlineData("lab", "up", "--topology", "single", "--json")]
    public void LabSyntaxErrorsUseTheExactUsage(params string[] arguments)
    {
        LiveLabCommandRunner runner = (_, _) =>
            throw new InvalidOperationException("Lab command should not run.");

        (int exitCode, string output, string error) = RunWithLab(runner, arguments);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            $"Usage: sdvkit lab <start|status|stop> --topology single --json{Environment.NewLine}",
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
}

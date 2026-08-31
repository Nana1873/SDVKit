using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record LiveLabCommandResult(int ExitCode, object Report);

internal delegate LiveLabCommandResult LiveLabCommandRunner(
    string action,
    string topology,
    string projectRoot);

internal delegate LiveLabCommandResult ProjectSmokeCommandRunner(
    string sourcePath,
    string topology,
    string labRoot);

public static class CliApplication
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int InspectionFailed = 3;
    private const string InspectUsage = "Usage: sdvkit project inspect [path] --json";
    private const string CreateUsage = "Usage: sdvkit project create <smapi-mod|content-pack> <path> --name <name> --author <author> --unique-id <id> --description <text> --json";
    private const string BuildUsage = "Usage: sdvkit project build [path] --json";
    private const string PackageUsage = "Usage: sdvkit project package [path] --json";
    private const string SmokeUsage =
        "Usage: sdvkit project smoke [path] --topology <single|network-2> --json";
    private const string LabSingleUsage =
        "Usage: sdvkit lab <start|status|stop|test-save> --topology single --json";
    private const string LabNetworkTwoUsage =
        "       sdvkit lab smoke --topology network-2 --json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        return Run(arguments, output, error, GameInstallationDiscovery.Discover);
    }

    internal static int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations,
        LiveLabCommandRunner? runLiveLab = null,
        ProjectSmokeCommandRunner? runProjectSmoke = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        if (runLiveLab is null)
        {
            runLiveLab = (action, topology, projectRoot) =>
                string.Equals(action, "smoke", StringComparison.Ordinal)
                && string.Equals(topology, "network-2", StringComparison.Ordinal)
                    ? NetworkTwoSmokeService.Execute(projectRoot, discoverInstallations)
                    : LiveLabService.Execute(action, projectRoot, discoverInstallations);
        }

        if (runProjectSmoke is null)
        {
            runProjectSmoke = (sourcePath, topology, labRoot) =>
                ProjectSmokeService.Execute(
                    sourcePath,
                    topology,
                    labRoot,
                    discoverInstallations);
        }

        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            WriteHelp(output);
            return Success;
        }

        if (IsVersion(arguments[0]))
        {
            return RunVersion(arguments, output, error);
        }

        if (string.Equals(arguments[0], "doctor", StringComparison.Ordinal))
        {
            return RunDoctor(arguments, output, error, discoverInstallations);
        }

        if (string.Equals(arguments[0], "project", StringComparison.Ordinal))
        {
            return RunProject(
                arguments,
                output,
                error,
                discoverInstallations,
                runProjectSmoke);
        }

        if (string.Equals(arguments[0], "lab", StringComparison.Ordinal))
        {
            return RunLab(arguments, output, error, runLiveLab);
        }

        error.WriteLine($"Unknown command '{arguments[0]}'. Run 'sdvkit help'.");
        return UsageError;
    }

    private static string CurrentVersion =>
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static int RunVersion(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        if (arguments.Count == 1)
        {
            output.WriteLine($"SDVKit {CurrentVersion}");
            return Success;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[1], "--json", StringComparison.Ordinal))
        {
            WriteJson(output, new
            {
                name = "sdvkit",
                version = CurrentVersion,
            });
            return Success;
        }

        error.WriteLine("Usage: sdvkit version [--json]");
        return UsageError;
    }

    private static int RunDoctor(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            output.WriteLine("Usage: sdvkit doctor --json");
            return Success;
        }

        if (arguments.Count != 2
            || !string.Equals(arguments[1], "--json", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: sdvkit doctor --json");
            return UsageError;
        }

        DoctorReport report = discoverInstallations();
        WriteJson(output, report);
        return string.Equals(report.Status, DoctorReport.Ready, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
    }

    private static int RunProject(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations,
        ProjectSmokeCommandRunner runProjectSmoke)
    {
        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            WriteProjectHelp(output);
            return Success;
        }

        if (arguments.Count < 2)
        {
            error.WriteLine(
                "Usage: sdvkit project <inspect|create|build|package|smoke> ...");
            return UsageError;
        }

        return arguments[1] switch
        {
            "inspect" => RunProjectInspect(arguments, output, error),
            "create" => RunProjectCreate(arguments, output, error),
            "build" => RunProjectBuild(arguments, output, error, discoverInstallations),
            "package" => RunProjectPackage(arguments, output, error, discoverInstallations),
            "smoke" => RunProjectSmoke(arguments, output, error, runProjectSmoke),
            _ => ProjectUsageError(error),
        };
    }

    private static int RunProjectInspect(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        if (arguments.Count == 3
            && IsHelp(arguments[2]))
        {
            output.WriteLine(InspectUsage);
            return Success;
        }

        if (!TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(InspectUsage);
            return UsageError;
        }

        ProjectInspectionReport report = ProjectInspector.Inspect(path!);
        WriteJson(output, report);
        return report.Problems.Count == 0
            && !string.Equals(report.Kind, ProjectInspectionReport.Unknown, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
    }

    private static int RunProjectCreate(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        if ((arguments.Count == 3 && IsHelp(arguments[2]))
            || (arguments.Count == 4 && IsHelp(arguments[3])))
        {
            output.WriteLine(CreateUsage);
            return Success;
        }

        if (!TryParseCreationRequest(arguments, out ProjectCreationRequest? request))
        {
            error.WriteLine(CreateUsage);
            return UsageError;
        }

        ProjectCreationReport report = ProjectCreator.Create(request!);
        WriteJson(output, report);
        return report.Problems.Count == 0 ? Success : InspectionFailed;
    }

    private static int RunProjectBuild(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(BuildUsage);
            return Success;
        }

        if (!TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(BuildUsage);
            return UsageError;
        }

        ProjectBuildReport report = ProjectBuilder.Build(path!, discoverInstallations);
        WriteJson(output, report);
        return report.Problems.Count == 0 ? Success : InspectionFailed;
    }

    private static int RunProjectPackage(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(PackageUsage);
            return Success;
        }

        if (!TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(PackageUsage);
            return UsageError;
        }

        ProjectPackageReport report = ProjectPackager.Package(path!, discoverInstallations);
        WriteJson(output, report);
        return report.Problems.Count == 0 ? Success : InspectionFailed;
    }

    private static int RunProjectSmoke(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectSmokeCommandRunner runProjectSmoke)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(SmokeUsage);
            return Success;
        }

        if (!TryParseProjectSmoke(arguments, out string? path, out string? topology))
        {
            error.WriteLine(SmokeUsage);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectSmoke(
            path!,
            topology!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseOptionalPath(
        IReadOnlyList<string> arguments,
        out string? path)
    {
        List<string> operands = [];
        var jsonOptionCount = 0;
        foreach (string argument in arguments.Skip(2))
        {
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
            }
            else
            {
                operands.Add(argument);
            }
        }

        if (jsonOptionCount != 1
            || operands.Count > 1
            || operands.Any(argument => argument.StartsWith('-')))
        {
            path = null;
            return false;
        }

        path = operands.Count == 0 ? Environment.CurrentDirectory : operands[0];
        return true;
    }

    private static bool TryParseProjectSmoke(
        IReadOnlyList<string> arguments,
        out string? path,
        out string? topology)
    {
        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        topology = null;
        for (var index = 2; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (string.Equals(argument, "--topology", StringComparison.Ordinal))
            {
                topologyOptionCount++;
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    path = null;
                    topology = null;
                    return false;
                }

                topology = arguments[++index];
                continue;
            }

            operands.Add(argument);
        }

        if (jsonOptionCount != 1
            || topologyOptionCount != 1
            || topology is not ("single" or "network-2")
            || operands.Count > 1
            || operands.Any(argument => argument.StartsWith('-')))
        {
            path = null;
            topology = null;
            return false;
        }

        path = operands.Count == 0 ? Environment.CurrentDirectory : operands[0];
        return true;
    }

    private static bool TryParseCreationRequest(
        IReadOnlyList<string> arguments,
        out ProjectCreationRequest? request)
    {
        request = null;
        if (arguments.Count < 5
            || (!string.Equals(arguments[2], ProjectCreator.SmapiMod, StringComparison.Ordinal)
                && !string.Equals(arguments[2], ProjectCreator.ContentPack, StringComparison.Ordinal))
            || arguments[3].StartsWith('-'))
        {
            return false;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var jsonOptionCount = 0;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (argument is not "--name" and not "--author" and not "--unique-id" and not "--description"
                || index + 1 >= arguments.Count
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(argument, arguments[++index]))
            {
                return false;
            }
        }

        if (jsonOptionCount != 1
            || !options.TryGetValue("--name", out string? name)
            || !options.TryGetValue("--author", out string? author)
            || !options.TryGetValue("--unique-id", out string? uniqueId)
            || !options.TryGetValue("--description", out string? description)
            || options.Count != 4)
        {
            return false;
        }

        request = new ProjectCreationRequest(
            arguments[2],
            arguments[3],
            name,
            author,
            uniqueId,
            description);
        return ProjectCreator.IsValidRequest(request);
    }

    private static int ProjectUsageError(TextWriter error)
    {
        error.WriteLine(
            "Usage: sdvkit project <inspect|create|build|package|smoke> ...");
        return UsageError;
    }

    private static int RunLab(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        LiveLabCommandRunner runLiveLab)
    {
        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            WriteLabUsage(output);
            return Success;
        }

        if (arguments.Count != 5)
        {
            WriteLabUsage(error);
            return UsageError;
        }

        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        string? topology = null;
        for (var index = 2; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (string.Equals(arguments[index], "--topology", StringComparison.Ordinal)
                && index + 1 < arguments.Count)
            {
                topologyOptionCount++;
                topology = arguments[++index];
                continue;
            }

            WriteLabUsage(error);
            return UsageError;
        }

        string action = arguments[1];
        bool isSingleCommand = string.Equals(topology, "single", StringComparison.Ordinal)
            && action is "start" or "status" or "stop" or "test-save";
        bool isNetworkTwoSmoke = string.Equals(topology, "network-2", StringComparison.Ordinal)
            && string.Equals(action, "smoke", StringComparison.Ordinal);
        if (jsonOptionCount != 1
            || topologyOptionCount != 1
            || (!isSingleCommand && !isNetworkTwoSmoke))
        {
            WriteLabUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runLiveLab(
            action,
            topology!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "help", StringComparison.Ordinal)
        || string.Equals(value, "--help", StringComparison.Ordinal)
        || string.Equals(value, "-h", StringComparison.Ordinal);

    private static bool IsVersion(string value) =>
        string.Equals(value, "version", StringComparison.Ordinal)
        || string.Equals(value, "--version", StringComparison.Ordinal);

    private static void WriteJson<T>(TextWriter output, T value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("SDVKit — lean Stardew Valley modding toolkit and live test lab");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  sdvkit help");
        output.WriteLine("  sdvkit version [--json]");
        output.WriteLine("  sdvkit doctor --json");
        output.WriteLine("  sdvkit project inspect [path] --json");
        output.WriteLine("  sdvkit project create <smapi-mod|content-pack> <path> [options] --json");
        output.WriteLine("  sdvkit project build [path] --json");
        output.WriteLine("  sdvkit project package [path] --json");
        output.WriteLine(
            "  sdvkit project smoke [path] --topology <single|network-2> --json");
        output.WriteLine("  sdvkit lab <start|status|stop|test-save> --topology single --json");
        output.WriteLine("  sdvkit lab smoke --topology network-2 --json");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  doctor          Detect ready Stardew Valley + SMAPI installations (read-only).");
        output.WriteLine("  project inspect Classify a SMAPI mod, content pack, or hybrid (read-only).");
        output.WriteLine("  project create  Create a minimal SMAPI mod or Content Patcher pack.");
        output.WriteLine("  project build   Build one SMAPI project with deployment disabled.");
        output.WriteLine("  project package Create an isolated release archive below .sdvkit/packages.");
        output.WriteLine("  project smoke   Build and smoke-test one mod in the isolated live lab.");
        output.WriteLine("  lab             Control one isolated process or run an isolated live-lab smoke.");
    }

    private static void WriteLabUsage(TextWriter output)
    {
        output.WriteLine(LabSingleUsage);
        output.WriteLine(LabNetworkTwoUsage);
    }

    private static void WriteProjectHelp(TextWriter output)
    {
        output.WriteLine("SDVKit project toolkit");
        output.WriteLine();
        output.WriteLine(InspectUsage);
        output.WriteLine(CreateUsage);
        output.WriteLine(BuildUsage);
        output.WriteLine(PackageUsage);
        output.WriteLine(SmokeUsage);
    }
}

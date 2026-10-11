using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int InspectionFailed = 3;

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
        Action<CliCommandRunners>? configureRunners = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        string? selectedGamePath = null;
        bool acceptsGamePath = arguments.Count > 0 && (arguments[0] == "doctor"
            || arguments.Count > 1 && arguments[0] == "lab" && arguments[1] is "start" or "test-save" or "smoke"
            || arguments.Count > 1 && arguments[0] == "project" && arguments[1] is "build" or "package" or "smoke"
            || arguments.Count > 2 && arguments[0] == "project" && arguments[1] == "review" && arguments[2] == "start");
        if (acceptsGamePath)
        {
            if (!TryExtractOption(arguments, "--game-path", out arguments, out string? gamePath))
            {
                error.WriteLine("Supply --game-path <directory> at most once.");
                return UsageError;
            }
            selectedGamePath = gamePath;
            Func<DoctorReport> originalDiscovery = discoverInstallations;
            var frozen = new Lazy<DoctorReport>(() => gamePath is null
                ? originalDiscovery()
                : GameInstallationDiscovery.Inspect([gamePath], includeMissingPaths: true));
            discoverInstallations = () => frozen.Value;
        }

        var runners = new CliCommandRunners(discoverInstallations, selectedGamePath);
        configureRunners?.Invoke(runners);

        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            WriteHelp(output);
            return Success;
        }

        return arguments[0] switch
        {
            "version" or "--version" => RunVersion(arguments, output, error),
            "doctor" => RunDoctor(arguments, output, error, discoverInstallations),
            "save" => RunSave(arguments, output, error),
            "project" => RunProject(arguments, output, error, discoverInstallations, runners),
            "lab" when arguments.Count > 1 && arguments[1] == "init" =>
                RunLabInit(arguments, output, error, discoverInstallations),
            "lab" => RunLab(arguments, output, error, runners.LiveLab),
            _ => UnknownCommand(arguments[0], error),
        };
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown command '{command}'. Run 'sdvkit help'.");
        return UsageError;
    }

    private static string CurrentVersion =>
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

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
            output.WriteLine("Usage: sdvkit doctor [--game-path <directory>] --json");
            return Success;
        }

        if (arguments.Count != 2
            || !string.Equals(arguments[1], "--json", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: sdvkit doctor [--game-path <directory>] --json");
            return UsageError;
        }

        DoctorReport report = discoverInstallations();
        WriteJson(output, report);
        return string.Equals(report.Status, DoctorReport.Ready, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
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

    private static void WriteJson<T>(TextWriter output, T value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }
}

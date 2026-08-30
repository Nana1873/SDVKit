using System.Text.Json;

namespace SdvKit.Cli;

public static class CliApplication
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
        Func<DoctorReport> discoverInstallations)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

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
            return RunProject(arguments, output, error);
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
        TextWriter error)
    {
        if (arguments.Count == 3
            && string.Equals(arguments[1], "inspect", StringComparison.Ordinal)
            && IsHelp(arguments[2]))
        {
            output.WriteLine("Usage: sdvkit project inspect [path] --json");
            return Success;
        }

        if (arguments.Count < 3
            || !string.Equals(arguments[1], "inspect", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: sdvkit project inspect [path] --json");
            return UsageError;
        }

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
            error.WriteLine("Usage: sdvkit project inspect [path] --json");
            return UsageError;
        }

        string path = operands.Count == 0 ? Environment.CurrentDirectory : operands[0];
        ProjectInspectionReport report = ProjectInspector.Inspect(path);
        WriteJson(output, report);
        return report.Problems.Count == 0
            && !string.Equals(report.Kind, ProjectInspectionReport.Unknown, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
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
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  doctor          Detect ready Stardew Valley + SMAPI installations (read-only).");
        output.WriteLine("  project inspect Classify a SMAPI mod, content pack, or hybrid (read-only).");
    }
}

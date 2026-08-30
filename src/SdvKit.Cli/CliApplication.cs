using System.Text.Json;

namespace SdvKit.Cli;

public static class CliApplication
{
    private const int Success = 0;
    private const int UsageError = 2;

    public static int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            WriteHelp(output);
            return Success;
        }

        if (IsVersion(arguments[0]))
        {
            if (arguments.Count == 1)
            {
                output.WriteLine($"SDVKit {CurrentVersion}");
                return Success;
            }

            if (arguments.Count == 2
                && string.Equals(arguments[1], "--json", StringComparison.Ordinal))
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    name = "sdvkit",
                    version = CurrentVersion,
                }));
                return Success;
            }

            error.WriteLine("Usage: sdvkit version [--json]");
            return UsageError;
        }

        error.WriteLine($"Unknown command '{arguments[0]}'. Run 'sdvkit help'.");
        return UsageError;
    }

    private static string CurrentVersion =>
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static bool IsHelp(string value) =>
        string.Equals(value, "help", StringComparison.Ordinal)
        || string.Equals(value, "--help", StringComparison.Ordinal)
        || string.Equals(value, "-h", StringComparison.Ordinal);

    private static bool IsVersion(string value) =>
        string.Equals(value, "version", StringComparison.Ordinal)
        || string.Equals(value, "--version", StringComparison.Ordinal);

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("SDVKit — lean Stardew Valley modding toolkit and live test lab");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  sdvkit help");
        output.WriteLine("  sdvkit version [--json]");
    }
}

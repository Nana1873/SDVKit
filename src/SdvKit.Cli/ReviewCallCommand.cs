using System.Text.Json;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record ProjectReviewCallRequest(
    string Tool,
    JsonElement? Arguments,
    string Topology,
    string? Role,
    int? ScreenId,
    bool AllowInput,
    bool AllowFixtureActions,
    bool AllowWorldActions,
    bool AllowContainerTransfer,
    bool AllowCpRefresh);

public static partial class CliApplication
{
    private const string ReviewCallUsage =
        "       sdvkit project review call <tool> ['<json-arguments>'] [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] [--allow-input] [--allow-fixture-actions] [--allow-world-actions] [--allow-container-transfer] [--allow-cp-refresh]";

    private static int RunProjectReviewCall(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 4 && IsHelp(arguments[3]))
        {
            WriteProjectReviewCallUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewCall(arguments, out ProjectReviewCallRequest? request, out string? parseError))
        {
            error.WriteLine(parseError ?? ReviewCallUsage.TrimStart());
            return UsageError;
        }

        return ProjectReviewMcpServer.CallAsync(request!, Environment.CurrentDirectory, output, error)
            .GetAwaiter()
            .GetResult();
    }

    private static void WriteProjectReviewCallUsage(TextWriter output)
    {
        output.WriteLine(ReviewCallUsage.TrimStart());
        output.WriteLine(
            "Calls one native MCP tool once against the active review, with the same selection, binding and opt-ins as 'project review mcp serve', and prints its structured result as JSON on stdout. Exit 0 means the tool succeeded. Exit 3 means the call failed: a tool error (its JSON is still printed), a startup or binding failure, a tool the selection and opt-ins do not provide, or a protocol failure (on stderr).");
        output.WriteLine(
            "The tool's arguments are one JSON object; omit it for tools without arguments. 'project review mcp serve --help' lists the tools for each selection and opt-in.");
    }

    /// <summary>
    /// Parses <c>project review call &lt;tool&gt; [json] [options]</c>. The options are exactly those of
    /// <c>mcp serve</c> without its transport options.
    /// </summary>
    internal static bool TryParseProjectReviewCall(
        IReadOnlyList<string> arguments,
        out ProjectReviewCallRequest? request,
        out string? parseError)
    {
        request = null;
        parseError = null;
        if (arguments.Count < 4
            || arguments[2] != "call"
            || arguments[3].StartsWith('-')
            || !IsToolName(arguments[3]))
        {
            return false;
        }

        string tool = arguments[3];
        var index = 4;
        JsonElement? toolArguments = null;
        if (index < arguments.Count && !arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(arguments[index]);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    parseError = "The tool arguments must be one JSON object.";
                    return false;
                }

                toolArguments = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                parseError = $"The tool arguments are not valid JSON: {exception.Message}";
                return false;
            }

            index++;
        }

        string[] serveArguments = ["project", "review", "mcp", "serve", .. arguments.Skip(index)];
        if (!TryParseProjectReviewMcp(
                serveArguments,
                out string? topology,
                out string? role,
                out int? screenId,
                out bool allowInput,
                out bool allowFixtureActions,
                out bool allowWorldActions,
                out bool allowContainerTransfer,
                out bool allowCpRefresh,
                out int? httpPort)
            || httpPort is not null)
        {
            return false;
        }

        request = new ProjectReviewCallRequest(
            tool,
            toolArguments,
            topology!,
            role,
            screenId,
            allowInput,
            allowFixtureActions,
            allowWorldActions,
            allowContainerTransfer,
            allowCpRefresh);
        return true;
    }

    private static bool IsToolName(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

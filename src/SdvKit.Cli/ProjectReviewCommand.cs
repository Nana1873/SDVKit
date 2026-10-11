using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static int RunProjectReview(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CliCommandRunners runners)
    {
        return (arguments.Count > 2 ? arguments[2] : null) switch
        {
            "split-screen" or "network" => RunProjectReviewLifecycle(arguments, output, error, runners.ProjectReviewConsole),
            "call" => RunProjectReviewCall(arguments, output, error),
            "cp-refresh" => RunProjectReviewCpRefresh(arguments, output, error),
            "config-reconcile" => RunProjectReviewConfigReconcile(arguments, output, error),
            "cp-diagnose" => RunProjectReviewCpDiagnosis(arguments, output, error),
            "progression" => RunProjectReviewProgression(arguments, output, error),
            "shop" => RunProjectReviewShop(arguments, output, error),
            "inventory" => RunProjectReviewInventory(arguments, output, error),
            "world" => RunProjectReviewWorld(arguments, output, error),
            "container-transfer" => RunProjectReviewContainerTransfer(arguments, output, error),
            "container" => RunProjectReviewContainer(arguments, output, error),
            "interact" => RunProjectReviewWorldAction(arguments, output, error),
            "act" => RunProjectReviewWorldAct(arguments, output, error),
            "menu" => RunProjectReviewMenu(arguments, output, error),
            "diagnostics" => RunProjectReviewDiagnostics(arguments, output, error),
            "data" => RunProjectReviewData(arguments, output, error, runners.ProjectReviewData),
            "map" => RunProjectReviewMap(arguments, output, error, runners.ProjectReviewMap),
            "texture" => RunProjectReviewTexture(arguments, output, error, runners.ProjectReviewTexture),
            "audio" => RunProjectReviewAudio(arguments, output, error, runners.ProjectReviewAudio),
            "mod-assets" => RunProjectReviewModAssets(arguments, output, error, runners.ProjectReviewModAsset),
            _ => RunProjectReviewSession(arguments, output, error, runners),
        };
    }

    private static int RunProjectReviewSession(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CliCommandRunners runners)
    {
        if (arguments.Count >= 4
            && string.Equals(arguments[2], "mcp", StringComparison.Ordinal)
            && ((arguments.Count == 4 && IsHelp(arguments[3]))
                || (arguments.Count == 5
                    && string.Equals(arguments[3], "serve", StringComparison.Ordinal)
                    && IsHelp(arguments[4]))))
        {
            WriteProjectReviewMcpUsage(output);
            return Success;
        }

        if (TryParseProjectReviewMcp(
                arguments,
                out string? mcpTopology,
                out string? mcpRole,
                out int? mcpScreenId,
                out bool allowInput,
                out bool allowFixtureActions,
                out bool allowWorldActions,
                out bool allowContainerTransfer,
                out bool allowCpRefresh,
                out int? httpPort))
        {
            return runners.ProjectReviewMcp(
                mcpTopology!,
                mcpRole,
                mcpScreenId,
                Environment.CurrentDirectory,
                allowInput,
                allowFixtureActions,
                allowWorldActions,
                allowContainerTransfer,
                allowCpRefresh,
                error,
                httpPort);
        }

        if ((arguments.Count == 3 && IsHelp(arguments[2]))
            || (arguments.Count == 4 && IsHelp(arguments[3])))
        {
            if (arguments.Count == 4
                && string.Equals(arguments[2], "command", StringComparison.Ordinal))
            {
                output.WriteLine(ReviewCommandUsage.TrimStart());
                WriteReviewConsoleCommandUsage(output);
            }
            else
            {
                WriteProjectReviewUsage(output);
            }

            return Success;
        }

        if (!TryExtractOption(arguments, "--project", out arguments, out string? projectFile)
            || (projectFile is not null && (arguments.Count < 3 || arguments[2] != "start")))
        {
            WriteProjectReviewUsage(error);
            return UsageError;
        }

        if (!TryParseProjectReview(
                arguments,
                out string? action,
                out string? path,
                out IReadOnlyList<string>? companionPaths,
                out IReadOnlyList<string>? contentPackPaths,
                out string? command,
                out string? topology,
                out string? role,
                out bool useTestSave))
        {
            WriteProjectReviewUsage(error);
            return UsageError;
        }

        if (string.Equals(action, "command", StringComparison.Ordinal)
            && IsSdvKitConsoleLine(command!)
            && !(string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
                && ProjectReviewConsoleLine.IsTitleInputOrViewport(command!)))
        {
            error.WriteLine(SdvKitConsoleLineRejection);
            return UsageError;
        }

        LiveLabCommandResult result = string.Equals(
            action,
            "command",
            StringComparison.Ordinal)
                ? runners.ProjectReviewConsole(
                    command!,
                    topology!,
                    role,
                    Environment.CurrentDirectory)
                : runners.ProjectReview(
                    action!,
                    path!,
                    companionPaths!,
                    contentPackPaths!,
                    useTestSave,
                    topology!,
                    Environment.CurrentDirectory,
                    projectFile);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReview(
        IReadOnlyList<string> arguments,
        out string? action,
        out string? path,
        out IReadOnlyList<string>? companionPaths,
        out IReadOnlyList<string>? contentPackPaths,
        out string? command,
        out string? topology,
        out string? role,
        out bool useTestSave)
    {
        action = arguments.Count > 2 ? arguments[2] : null;
        path = null;
        companionPaths = null;
        contentPackPaths = null;
        command = null;
        topology = "single";
        role = null;
        useTestSave = false;
        if (action is not ("start" or "status" or "command" or "stop" or "reset"))
        {
            return false;
        }

        var operands = new List<string>();
        var companions = new List<string>();
        var packs = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var roleOptionCount = 0;
        var testSaveOptionCount = 0;
        for (var index = 3; index < arguments.Count; index++)
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
                    return false;
                }

                topology = arguments[++index];
                continue;
            }

            if (string.Equals(argument, "--role", StringComparison.Ordinal))
            {
                roleOptionCount++;
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                role = arguments[++index];
                continue;
            }

            if (string.Equals(argument, "--test-save", StringComparison.Ordinal))
            {
                testSaveOptionCount++;
                useTestSave = true;
                continue;
            }

            if (argument is "--companion" or "--content-pack")
            {
                if (!string.Equals(action, "start", StringComparison.Ordinal)
                    || index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                (string.Equals(argument, "--companion", StringComparison.Ordinal)
                    ? companions
                    : packs).Add(value);
                continue;
            }

            operands.Add(argument);
        }

        bool isStart = string.Equals(action, "start", StringComparison.Ordinal);
        bool isCommand = string.Equals(action, "command", StringComparison.Ordinal);
        bool isReset = string.Equals(action, "reset", StringComparison.Ordinal);
        bool networkTwo = string.Equals(topology, "network-2", StringComparison.Ordinal);
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || topology is not ("single" or "network-2")
            || roleOptionCount > 1
            || testSaveOptionCount > 1
            || (role is not null && role is not ("host" or "farmhand"))
            || (isStart && operands.Count > 1)
            || (isCommand && (operands.Count != 1
                || ProjectReviewConsoleLine.ValidationError(operands[0]) is not null))
            || (!isStart && !isCommand && operands.Count > 0)
            || (!isStart && (companions.Count > 0 || packs.Count > 0))
            || (!isCommand && roleOptionCount > 0)
            || (isCommand && networkTwo && roleOptionCount != 1)
            || (isCommand && !networkTwo && roleOptionCount != 0)
            || (testSaveOptionCount > 0 && (!isStart || networkTwo))
            || (isReset && topologyOptionCount != 1)
            || (isStart && operands.Any(argument => argument.StartsWith('-'))))
        {
            return false;
        }

        path = isStart && operands.Count == 1
            ? operands[0]
            : Environment.CurrentDirectory;
        companionPaths = companions;
        contentPackPaths = packs;
        command = isCommand ? operands[0] : null;
        return true;
    }
}

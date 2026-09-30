using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private static bool TryParseProjectReviewMcp(
        IReadOnlyList<string> arguments,
        out string? topology,
        out string? role,
        out int? screenId,
        out bool allowInput,
        out bool allowFixtureActions,
        out bool allowWorldActions,
        out bool allowContainerTransfer,
        out bool allowCpRefresh)
    {
        topology = LiveLabState.SingleTopology;
        role = null;
        screenId = null;
        allowInput = false;
        allowFixtureActions = false;
        allowWorldActions = false;
        allowContainerTransfer = false;
        allowCpRefresh = false;
        if (arguments.Count < 4
            || !string.Equals(arguments[2], "mcp", StringComparison.Ordinal)
            || !string.Equals(arguments[3], "serve", StringComparison.Ordinal))
        {
            return false;
        }

        var topologyCount = 0;
        var roleCount = 0;
        var screenCount = 0;
        var allowInputCount = 0;
        var allowFixtureActionsCount = 0;
        var allowWorldActionsCount = 0;
        var allowContainerTransferCount = 0;
        var allowCpRefreshCount = 0;
        for (var index = 4; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (option == "--allow-cp-refresh")
            {
                allowCpRefreshCount++;
                allowCpRefresh = true;
                continue;
            }
            if (string.Equals(option, "--allow-input", StringComparison.Ordinal))
            {
                allowInputCount++;
                allowInput = true;
                continue;
            }

            if (string.Equals(
                    option,
                    "--allow-fixture-actions",
                    StringComparison.Ordinal))
            {
                allowFixtureActionsCount++;
                allowFixtureActions = true;
                continue;
            }
            if (string.Equals(option, "--allow-world-actions", StringComparison.Ordinal))
            {
                allowWorldActionsCount++;
                allowWorldActions = true;
                continue;
            }
            if (string.Equals(option, "--allow-container-transfer", StringComparison.Ordinal))
            {
                allowContainerTransferCount++;
                allowContainerTransfer = true;
                continue;
            }

            if (index + 1 >= arguments.Count
                || arguments[index + 1].StartsWith('-'))
            {
                return false;
            }

            string value = arguments[++index];
            if (string.Equals(option, "--topology", StringComparison.Ordinal))
            {
                topologyCount++;
                topology = value;
            }
            else if (string.Equals(option, "--role", StringComparison.Ordinal))
            {
                roleCount++;
                role = value;
            }
            else if (string.Equals(option, "--screen", StringComparison.Ordinal)
                && int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsedScreen)
                && parsedScreen >= 0)
            {
                screenCount++;
                screenId = parsedScreen;
            }
            else
            {
                return false;
            }
        }

        if (topologyCount > 1
            || roleCount > 1
            || screenCount > 1
            || allowInputCount > 1
            || allowFixtureActionsCount > 1
            || allowWorldActionsCount > 1
            || allowContainerTransferCount > 1
            || allowCpRefreshCount > 1
            || screenId is not null && (allowFixtureActions || allowWorldActions || allowContainerTransfer || allowCpRefresh)
            || (allowCpRefresh || allowWorldActions || allowContainerTransfer) && topology != LiveLabState.SingleTopology)
        {
            return false;
        }

        return string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            ? roleCount == 0 && (screenCount == 0 || screenId is not null)
            : string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                && topologyCount == 1
                && roleCount == 1
                && screenCount == 0
                && NetworkTwoContract.IsRole(role!);
    }
}

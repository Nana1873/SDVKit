#if SDVKIT_GAME_AVAILABLE
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.GameSide;

/// <summary>
/// The review selection a world action or container transfer runs on: the unbound single review on screen 0, one
/// network-2 role, or one selected local split-screen screen. Each action checks that the selection it was bound
/// to is the one this process and screen serve, and that the exact disposable fixture is verified for the current
/// player, before reading or touching anything else.
/// </summary>
internal sealed record ReviewActionSelection(string? Role, int? ScreenId)
{
    internal static bool TryParse(string token, out ReviewActionSelection selection)
    {
        bool parsed = ReviewWorldActionContract.TryParseSelection(token, out string? role, out int? screenId);
        selection = new(role, screenId);
        return parsed;
    }

    /// <summary>The network-2 role this process serves, or null in a single review.</summary>
    internal static string? ProcessRole =>
        Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")?.Trim() is { Length: > 0 } role ? role : null;

    internal string Topology => Role is null ? "single" : NetworkTwoContract.Topology;

    internal bool BindingReady(string launch) =>
        Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
        && ReviewTransportToken.IsRequestId(launch)
        && ProcessRole == Role
        && (Role is null || ScreenId is null && !Context.IsSplitScreen)
        && Context.ScreenId == (ScreenId ?? 0);

    internal bool FixtureReady(Func<TestSaveAutomation?> testSave, Func<NetworkTwoAutomation?> networkTwo)
    {
        if (Role is not null)
        {
            if (networkTwo() is not { } network
                || !network.TryVerifyReviewFixture(out _, out string verifiedRole, out _, out _)
                || verifiedRole != Role || network.IsHost != Context.IsMainPlayer)
                return false;
            return !network.IsHost || testSave() is { } host && host.TryVerifyReviewFixture(out _, out _);
        }
        if (testSave() is not { } fixture) return false;
        if (ScreenId is null) return fixture.TryVerifyReviewFixture(out _, out _);
        try
        {
            fixture.VerifyCurrentLocalScreen();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
#endif

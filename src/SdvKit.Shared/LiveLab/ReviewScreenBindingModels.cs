namespace SdvKit.Cli.LiveLab;

internal static class ReviewScreenBindingContract
{
    public const int SchemaVersion = 1;
    public const int MaximumResponseBytes = 16 * 1024;

    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-screen-{requestId}.json");

    public static bool TryValidateDispatch(
        string[] arguments,
        string currentFarmerId,
        string currentContextId,
        out string[] commandArguments)
    {
        commandArguments = arguments;
        int bindingIndex = Array.FindLastIndex(arguments,
            value => value.StartsWith("binding=", StringComparison.Ordinal));
        int farmerIndex = Array.FindLastIndex(arguments,
            value => value.StartsWith("farmer=", StringComparison.Ordinal));
        if (bindingIndex < 0 && farmerIndex < 0) return true;
        if (bindingIndex < 0 || farmerIndex < 0
            || bindingIndex != arguments.Length - 2
            || farmerIndex != arguments.Length - 1
            || !ReviewTransportToken.IsRequestId(currentContextId)
            || !string.Equals(arguments[bindingIndex]["binding=".Length..], currentContextId,
                StringComparison.Ordinal)
            || !string.Equals(arguments[farmerIndex]["farmer=".Length..], currentFarmerId,
                StringComparison.Ordinal))
        {
            return false;
        }
        commandArguments = arguments[..bindingIndex];
        return true;
    }
}

internal sealed record ReviewScreenBindingReport(
    int SchemaVersion,
    string LaunchId,
    string FixtureId,
    int ScreenId,
    string FarmerId,
    string ContextId,
    DateTimeOffset ObservedAtUtc,
    RuntimeSnapshotMarker Runtime);

internal sealed record ReviewScreenBindingResponse(
    int SchemaVersion,
    string RequestId,
    ReviewScreenBindingReport Report);

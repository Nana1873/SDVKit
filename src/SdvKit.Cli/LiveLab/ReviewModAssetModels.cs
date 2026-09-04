using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewModAssetContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageLimit = 50;
    public const int MaximumPageLimit = 100;
    public const int MaximumObservedAssets = 2048;
    public const int MaximumAssetLength = 512;
    public const int MaximumKeyLength = 480;
    public const int MaximumRecordsPerAsset = 10_000;
    public const int MaximumStringValueLength = 64 * 1024;
    public const int MaximumAdaptedPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumResponseBytes = 5 * 1024 * 1024;
    public const int MaximumVersionLength = 128;
    public const int MaximumDataTypeLength = 512;
    public const int MaximumIdentityStatusLength = 64;
    public const int MaximumShapeLength = 64;
    public const int MaximumLifecycleLength = 32;
    public const int MaximumProblemCount = 8;
    public const int MaximumProblemCodeLength = 64;
    public const int MaximumProblemMessageLength = 512;
    public const string AssetsOperation = "assets";
    public const string KeysOperation = "keys";
    public const string GetOperation = "get";
    public const string SingletonKey = "singleton";
    public const string CoverageScope = "observedRequestsSinceAlwaysOnSubscribed";

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-mod-assets runtime path is required.",
                nameof(runtimePath));
        }
        if (!IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-mod-assets request ID is invalid.",
                nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-mod-assets-{requestId}.json");
    }

    public static bool IsRequestId(string? value) =>
        ReviewTransportToken.IsRequestId(value);

    public static string Encode(string value) => ReviewTransportToken.Encode(value);

    public static bool TryDecode(string token, int maximumLength, out string value) =>
        ReviewTransportToken.TryDecode(token, maximumLength, out value);

    public static bool IsCanonicalAssetName(string? value)
    {
        if (!IsBoundedText(value, MaximumAssetLength)
            || !string.Equals(value, value!.Trim(), StringComparison.Ordinal)
            || value.Contains('\\'))
        {
            return false;
        }

        string[] segments = value.Split('/');
        return segments.Length >= 3
            && string.Equals(segments[0], "Mods", StringComparison.Ordinal)
            && segments.All(segment =>
                segment.Length > 0
                && segment is not "." and not ".."
                && StableIdentityNormalizer.Normalize(segment).Length > 0);
    }

    public static bool IsBoundedText(string? value, int maximumLength) =>
        value is not null
        && value.Length is > 0
        && value.Length <= maximumLength
        && !value.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(value);

    public static bool AssetIdentityEquals(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/'),
            right.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    public static bool StableAssetIdentityEquals(string left, string right)
    {
        string[] leftSegments = left.Replace('\\', '/').Split('/');
        string[] rightSegments = right.Replace('\\', '/').Split('/');
        if (leftSegments.Length < 3
            || leftSegments.Length != rightSegments.Length
            || !string.Equals(leftSegments[0], "Mods", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rightSegments[0], "Mods", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(leftSegments[1], rightSegments[1], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var index = 2; index < leftSegments.Length; index++)
        {
            if (!string.Equals(
                    StableIdentityNormalizer.Normalize(leftSegments[index]),
                    StableIdentityNormalizer.Normalize(rightSegments[index]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static string StableAssetIdentityKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] segments = value.Replace('\\', '/').Split('/');
        if (segments.Length < 3)
        {
            return string.Empty;
        }

        return string.Join(
            '/',
            "mods",
            segments[1].ToUpperInvariant(),
            string.Join('/', segments.Skip(2).Select(StableIdentityNormalizer.Normalize)));
    }
}

internal sealed record ReviewModAssetQuery(
    string Operation,
    string? Asset,
    string? Key,
    int Offset,
    int Limit);

internal sealed record ReviewModAssetProblem(string Code, string Message);

internal sealed record ReviewModAssetAssetReport(
    string AssetName,
    string? NamespaceOwnerId,
    string NamespaceOwnerStatus,
    string? ProviderModId,
    string ProviderStatus,
    string DataType,
    string? Shape,
    string Lifecycle,
    int Generation,
    int RequestCount,
    int ReadyCount,
    bool Available,
    bool AdapterSupported,
    bool NameCollision,
    bool TypeCollision,
    string? ProblemCode);

internal sealed record ReviewModAssetPage(
    int Offset,
    int Limit,
    int Returned,
    int Total,
    int? NextOffset);

internal sealed record ReviewModAssetCoverageReport(
    string Scope,
    DateTimeOffset ObservationStartedAtUtc,
    int Observed,
    int Catalogued,
    int AdapterSupported,
    int AdapterUnavailable,
    int Ready,
    int Invalidated,
    int Unavailable,
    int NameCollisions,
    int TypeCollisions,
    int Dropped)
{
    public bool Complete =>
        string.Equals(
            Scope,
            ReviewModAssetContract.CoverageScope,
            StringComparison.Ordinal)
        && Observed == Catalogued
        && Dropped == 0;
}

internal sealed record ReviewModAssetReport(
    int SchemaVersion,
    string State,
    string Operation,
    string? GameVersion,
    string? GameFileVersion,
    string CoverageScope,
    ReviewModAssetAssetReport? Asset,
    string? Key,
    IReadOnlyList<ReviewModAssetAssetReport>? Assets,
    IReadOnlyList<string>? Keys,
    ReviewModAssetPage? Page,
    ReviewModAssetCoverageReport? Coverage,
    JsonElement? Record,
    IReadOnlyList<ReviewModAssetProblem> Problems);

internal sealed record ReviewModAssetResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewModAssetReport Report);

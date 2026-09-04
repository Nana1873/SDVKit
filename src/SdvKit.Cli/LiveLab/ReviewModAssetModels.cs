using System.Text;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewModAssetContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageLimit = 50;
    public const int MaximumPageLimit = 100;
    public const int MaximumObservedAssets = 2048;
    public const int MaximumAssetLength = 512;
    public const int MaximumKeyLength = 2048;
    public const int MaximumRecordsPerAsset = 10_000;
    public const int MaximumStringValueLength = 64 * 1024;
    public const int MaximumAdaptedPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumResponseBytes = 5 * 1024 * 1024;
    public const string AssetsOperation = "assets";
    public const string KeysOperation = "keys";
    public const string GetOperation = "get";
    public const string SingletonKey = "singleton";
    public const string CoverageScope = "observedRequestsSinceAlwaysOnSubscribed";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

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
        value is not null && Guid.TryParseExact(value, "N", out _);

    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToBase64String(StrictUtf8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string token, int maximumLength, out string value)
    {
        ArgumentNullException.ThrowIfNull(token);
        value = string.Empty;
        if (token.Length == 0
            || token.Any(character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            return false;
        }

        string padded = token.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "\0",
        };
        if (padded[^1] == '\0')
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(Convert.FromBase64String(padded));
        }
        catch (Exception exception) when (exception is FormatException
            or DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }

        if (value.Length == 0
            || value.Length > maximumLength
            || value.Any(char.IsControl)
            || !string.Equals(Encode(value), token, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        return true;
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

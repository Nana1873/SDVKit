using System.Text;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewDataContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageLimit = 50;
    public const int MaximumPageLimit = 100;
    public const int MaximumAssetLength = 256;
    public const int MaximumKeyLength = 2048;
    public const int MaximumRecordBytes = 4 * 1024 * 1024;
    public const int MaximumResponseBytes = 5 * 1024 * 1024;
    public const string AssetsOperation = "assets";
    public const string KeysOperation = "keys";
    public const string GetOperation = "get";
    public const string SingletonKey = "singleton";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-data runtime path is required.",
                nameof(runtimePath));
        }
        if (!IsRequestId(requestId))
        {
            throw new ArgumentException("The review-data request ID is invalid.", nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-data-{requestId}.json");
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

internal sealed record ReviewDataQuery(
    string Operation,
    string? Asset,
    string? Key,
    int Offset,
    int Limit);

internal sealed record ReviewDataProblem(
    string Code,
    string Message);

internal sealed record ReviewDataAssetReport(
    string AssetName,
    string? DataType,
    string? Shape,
    string? KeyKind,
    int? RecordCount,
    bool Supported,
    string? ProblemCode);

internal sealed record ReviewDataPage(
    int Offset,
    int Limit,
    int Returned,
    int Total,
    int? NextOffset);

internal sealed record ReviewDataCoverageReport(
    int Discovered,
    int Classified,
    int Supported,
    int Unknown,
    int Unclassified,
    int Unsupported)
{
    public bool Complete =>
        Discovered == Classified
        && Classified == Supported
        && Unknown == 0
        && Unclassified == 0
        && Unsupported == 0;
}

internal sealed record ReviewDataReport(
    int SchemaVersion,
    string State,
    string Operation,
    string? GameVersion,
    string? GameFileVersion,
    string? AssetName,
    string? DataType,
    string? Shape,
    string? KeyKind,
    string? Key,
    IReadOnlyList<ReviewDataAssetReport>? Assets,
    IReadOnlyList<string>? Keys,
    ReviewDataPage? Page,
    ReviewDataCoverageReport? Coverage,
    JsonElement? Record,
    IReadOnlyList<ReviewDataProblem> Problems);

internal sealed record ReviewDataResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewDataReport Report);

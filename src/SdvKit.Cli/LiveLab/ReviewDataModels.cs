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

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-data runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException("The review-data request ID is invalid.", nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-data-{requestId}.json");
    }

}

internal static class StableIdentityNormalizer
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var token = new StringBuilder(value.Length);
        var pendingSeparator = false;
        foreach (char character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && token.Length > 0)
                {
                    token.Append('-');
                }

                token.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return token.ToString();
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

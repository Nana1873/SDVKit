namespace SdvKit.Cli.LiveLab;

internal static class ReviewTextureContract
{
    public const int SchemaVersion = 1;
    public const int DefaultPageLimit = 50;
    public const int MaximumPageLimit = 100;
    public const int MaximumDiscoveredAssets = 8192;
    public const int MaximumAssetLength = 512;
    public const int MaximumSourceDimension = 8192;
    public const int MaximumSourcePixels = 16 * 1024 * 1024;
    public const int MaximumPreviewDimension = 512;
    public const int MaximumPreviewPixels = MaximumPreviewDimension * MaximumPreviewDimension;
    public const int MaximumPreviewBytes = 2 * 1024 * 1024;
    public const int MaximumResponseBytes = 5 * 1024 * 1024;
    public const int MaximumRuntimeFormatLength = 128;
    public const int MaximumVersionLength = 128;
    public const int MaximumProblemCount = 8;
    public const int MaximumProblemCodeLength = 64;
    public const int MaximumProblemMessageLength = 512;

    public const string AssetsOperation = "assets";
    public const string GetOperation = "get";
    public const string PreviewOperation = "preview";
    public const string CanonicalGameContentSource = "canonical-game-content";
    public const string FinalPipelineStage = "final-post-pipeline";
    public const string ProvenanceUnavailableDetail =
        "The supported SMAPI content API exposes the final texture but not its per-mod loader or editor chain.";

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-texture runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-texture request ID is invalid.",
                nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-texture-{requestId}.json");
    }

    public static string PreviewFileName(string requestId)
    {
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-texture request ID is invalid.",
                nameof(requestId));
        }

        return $"review-texture-preview-{requestId}.png";
    }

    public static string PreviewPath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, PreviewFileName(requestId));

    public static (int Width, int Height) PreviewDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Texture dimensions must be positive.");
        }

        if (width <= MaximumPreviewDimension
            && height <= MaximumPreviewDimension)
        {
            return (width, height);
        }

        return width >= height
            ? (
                MaximumPreviewDimension,
                Math.Max(
                    1,
                    checked((int)((long)height * MaximumPreviewDimension / width))))
            : (
                Math.Max(
                    1,
                    checked((int)((long)width * MaximumPreviewDimension / height))),
                MaximumPreviewDimension);
    }

    public static bool IsCanonicalAssetName(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)
            || assetName.Length > MaximumAssetLength
            || assetName.Any(char.IsControl)
            || assetName.Contains('\\')
            || assetName.StartsWith('/')
            || assetName.EndsWith('/'))
        {
            return false;
        }

        return assetName
            .Split('/')
            .All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    public static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}

internal sealed record ReviewTextureQuery(
    string Operation,
    string? Asset,
    int Offset,
    int Limit);

internal sealed record ReviewTextureProblem(string Code, string Message);

internal sealed record ReviewTextureAssetReport(
    string AssetName,
    string SourceCategory,
    bool Available);

internal sealed record ReviewTextureMetadataReport(
    int Width,
    int Height,
    string RuntimeFormat,
    int LevelCount,
    bool HasMipMaps);

internal sealed record ReviewTextureProvenanceReport(
    string PipelineStage,
    bool DetailedProviderAvailable,
    string Detail);

internal sealed record ReviewTexturePreviewReport(
    string RelativePath,
    int Width,
    int Height,
    long EncodedBytes,
    string Sha256);

internal sealed record ReviewTexturePage(
    int Offset,
    int Limit,
    int Returned,
    int Total,
    int? NextOffset);

internal sealed record ReviewTextureCoverageReport(
    int Candidates,
    int Classified,
    int Textures,
    int NonTextures,
    int Gaps)
{
    public bool Complete =>
        Candidates == Classified + Gaps
        && Classified == Textures + NonTextures
        && Gaps == 0;
}

internal sealed record ReviewTextureReport(
    int SchemaVersion,
    string State,
    string Operation,
    string? GameVersion,
    string? GameFileVersion,
    string? AssetName,
    string? SourceCategory,
    bool? Available,
    ReviewTextureMetadataReport? Metadata,
    ReviewTextureProvenanceReport? Provenance,
    ReviewTexturePreviewReport? Preview,
    IReadOnlyList<ReviewTextureAssetReport>? Assets,
    ReviewTexturePage? Page,
    ReviewTextureCoverageReport? Coverage,
    IReadOnlyList<ReviewTextureProblem> Problems);

internal sealed record ReviewTextureResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    ReviewTextureReport Report);

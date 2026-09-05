using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal interface IReviewTextureAsset
{
    int Width { get; }

    int Height { get; }

    string RuntimeFormat { get; }

    int LevelCount { get; }

    void WriteNearestNeighborPng(Stream output, int width, int height);
}

internal interface IReviewTextureSource
{
    string GameVersion { get; }

    string GameFileVersion { get; }

    IReadOnlyList<string> DiscoverCanonicalAssetNames();

    bool TryClassifyTexture(
        string assetName,
        long maximumInputBytes,
        out bool isTexture,
        out long inputBytes);

    IReviewTextureAsset LoadTexture(string assetName);
}

internal static class ReviewTextureFileInventory
{
    internal const int MaximumVisitedEntries =
        ReviewTextureContract.MaximumDiscoveredAssets * 4;

    public static IReadOnlyList<string> Discover(
        string contentRoot,
        Func<string, bool> isLocalizedAsset,
        int maximumCandidates = ReviewTextureContract.MaximumDiscoveredAssets,
        int maximumVisitedEntries = MaximumVisitedEntries)
    {
        if (string.IsNullOrEmpty(contentRoot))
        {
            throw new ArgumentException(
                "The canonical content root is required.",
                nameof(contentRoot));
        }
        if (maximumCandidates is < 1
            or > ReviewTextureContract.MaximumDiscoveredAssets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidates),
                $"The discovery limit must be between 1 and {ReviewTextureContract.MaximumDiscoveredAssets}.");
        }
        ArgumentNullException.ThrowIfNull(isLocalizedAsset);
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVisitedEntries, 1);
#else
        if (maximumVisitedEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVisitedEntries));
        }
#endif

        string absoluteContentRoot = Path.GetFullPath(contentRoot);
        RefuseReparseDirectory(absoluteContentRoot);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        var visitedEntries = 0;
        pending.Push(absoluteContentRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                visitedEntries++;
                if (visitedEntries > maximumVisitedEntries)
                {
                    throw new ReviewTextureInventoryTooLargeException();
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The installed content tree contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (!string.Equals(
                        Path.GetExtension(entry),
                        ".xnb",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path
                    .GetRelativePath(absoluteContentRoot, entry)
                    .Replace('\\', '/');
                string assetName = relative[..^Path.GetExtension(relative).Length];
                if (!isLocalizedAsset(assetName)
                    && names.Add(assetName)
                    && names.Count > maximumCandidates)
                {
                    throw new ReviewTextureInventoryTooLargeException();
                }
            }
        }

        return names
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void RefuseReparseDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The installed content root is not a regular directory.");
        }
    }
}

internal sealed class ReviewTextureInventoryTooLargeException : IOException
{
}

internal static class ReviewTextureSampling
{
    public static void CopyNearestNeighbor<T>(
        T[] source,
        int sourceWidth,
        int sourceHeight,
        T[] destination,
        int destinationWidth,
        int destinationHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (sourceWidth <= 0
            || sourceHeight <= 0
            || destinationWidth <= 0
            || destinationHeight <= 0
            || destinationWidth > sourceWidth
            || destinationHeight > sourceHeight
            || source.Length != checked(sourceWidth * sourceHeight)
            || destination.Length != checked(destinationWidth * destinationHeight))
        {
            throw new ArgumentException(
                "The source and destination must describe exact bounded non-upscaled images.");
        }

        for (var y = 0; y < destinationHeight; y++)
        {
            int sourceY = checked((int)((long)y * sourceHeight / destinationHeight));
            for (var x = 0; x < destinationWidth; x++)
            {
                int sourceX = checked((int)((long)x * sourceWidth / destinationWidth));
                destination[(y * destinationWidth) + x] =
                    source[(sourceY * sourceWidth) + sourceX];
            }
        }
    }
}

internal static class ReviewTextureOperation
{
    private enum CandidateKind
    {
        Texture,
        NonTexture,
        Gap,
    }

    private sealed record Candidate(string AssetName, CandidateKind Kind);

    public static ReviewTextureReport Execute(
        ReviewTextureQuery query,
        IReviewTextureSource source,
        string runtimePath,
        string requestId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(source);

        ReviewTextureProblem? requestProblem = Validate(query);
        if (requestProblem is not null)
        {
            return Failure(query.Operation, source, requestProblem);
        }

        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "textureTransportInvalid",
                    "The bounded review-texture transport request is invalid."));
        }

        if (!TryInventory(source, out Candidate[]? candidates, out ReviewTextureProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        return query.Operation switch
        {
            ReviewTextureContract.AssetsOperation =>
                ListAssets(query, source, candidates!),
            ReviewTextureContract.GetOperation =>
                GetTexture(query, source, candidates!, runtimePath, requestId, preview: false),
            ReviewTextureContract.PreviewOperation =>
                GetTexture(query, source, candidates!, runtimePath, requestId, preview: true),
            _ => Failure(
                query.Operation,
                source,
                Problem("textureOperationUnknown", "The review-texture operation is unknown.")),
        };
    }

    public static ReviewTextureReport Failure(
        string operation,
        IReviewTextureSource source,
        params ReviewTextureProblem[] problems) =>
        new(
            ReviewTextureContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            problems);

    internal static bool TryDeletePreview(string runtimePath, string requestId)
    {
        try
        {
            string absoluteRuntimePath = Path.GetFullPath(runtimePath);
            FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
            if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
                || (runtimeAttributes & FileAttributes.Directory) == 0)
            {
                return false;
            }

            string path = ReviewTextureContract.PreviewPath(
                absoluteRuntimePath,
                requestId);
            if (!File.Exists(path))
            {
                return true;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return false;
        }
    }

    private static ReviewTextureReport ListAssets(
        ReviewTextureQuery query,
        IReviewTextureSource source,
        IReadOnlyList<Candidate> candidates)
    {
        ReviewTextureCoverageReport coverage = Coverage(candidates);
        ReviewTextureAssetReport[] textures = candidates
            .Where(candidate => candidate.Kind == CandidateKind.Texture)
            .Select(candidate => new ReviewTextureAssetReport(
                candidate.AssetName,
                ReviewTextureContract.CanonicalGameContentSource,
                true))
            .ToArray();
        ReviewTextureAssetReport[] page = textures
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();

        return new ReviewTextureReport(
            ReviewTextureContract.SchemaVersion,
            coverage.Complete ? "ready" : "blocked",
            query.Operation,
            source.GameVersion,
            source.GameFileVersion,
            null,
            ReviewTextureContract.CanonicalGameContentSource,
            null,
            null,
            Provenance(),
            null,
            page,
            Page(query, page.Length, textures.Length),
            coverage,
            coverage.Complete
                ? []
                : [Problem(
                    "textureCoverageIncomplete",
                    "The installed canonical content inventory contains one or more classification gaps.")]);
    }

    private static ReviewTextureReport GetTexture(
        ReviewTextureQuery query,
        IReviewTextureSource source,
        IReadOnlyList<Candidate> candidates,
        string runtimePath,
        string requestId,
        bool preview)
    {
        if (!TryResolveTexture(
                query.Asset!,
                candidates,
                out string? assetName,
                out ReviewTextureProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        IReviewTextureAsset texture;
        try
        {
            texture = source.LoadTexture(assetName!);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return AssetFailure(
                query.Operation,
                source,
                assetName!,
                false,
                Problem(
                    "textureAssetLoadFailed",
                    $"The selected final texture could not be loaded safely ({exception.GetType().Name})."));
        }

        if (!TryMetadata(texture, out ReviewTextureMetadataReport? metadata))
        {
            return AssetFailure(
                query.Operation,
                source,
                assetName!,
                true,
                Problem(
                    "textureMetadataInvalid",
                    "The selected final texture exposes invalid or unsupported metadata."));
        }

        if (!preview)
        {
            return Success(
                query.Operation,
                source,
                assetName!,
                metadata!,
                preview: null);
        }

        ReviewTexturePreviewReport? previewReport = TryCreatePreview(
            texture,
            runtimePath,
            requestId,
            out problem);
        if (previewReport is null)
        {
            return AssetFailure(
                query.Operation,
                source,
                assetName!,
                true,
                problem!,
                metadata);
        }

        return Success(
            query.Operation,
            source,
            assetName!,
            metadata!,
            previewReport);
    }

    private static ReviewTextureReport Success(
        string operation,
        IReviewTextureSource source,
        string assetName,
        ReviewTextureMetadataReport metadata,
        ReviewTexturePreviewReport? preview) =>
        new(
            ReviewTextureContract.SchemaVersion,
            "ready",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            assetName,
            ReviewTextureContract.CanonicalGameContentSource,
            true,
            metadata,
            Provenance(),
            preview,
            null,
            null,
            null,
            []);

    private static ReviewTextureReport AssetFailure(
        string operation,
        IReviewTextureSource source,
        string assetName,
        bool available,
        ReviewTextureProblem problem,
        ReviewTextureMetadataReport? metadata = null) =>
        new(
            ReviewTextureContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            assetName,
            ReviewTextureContract.CanonicalGameContentSource,
            available,
            metadata,
            Provenance(),
            null,
            null,
            null,
            null,
            [problem]);

    private static bool TryInventory(
        IReviewTextureSource source,
        out Candidate[]? candidates,
        out ReviewTextureProblem? problem)
    {
        IReadOnlyList<string> discovered;
        try
        {
            discovered = source.DiscoverCanonicalAssetNames()
                ?? throw new InvalidDataException(
                    "The canonical content inventory was unavailable.");
        }
        catch (ReviewTextureInventoryTooLargeException)
        {
            candidates = null;
            problem = InventoryTooLargeProblem();
            return false;
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            candidates = null;
            problem = Problem(
                "textureInventoryFailed",
                $"The installed canonical content inventory could not be read ({exception.GetType().Name}).");
            return false;
        }

        if (discovered.Count > ReviewTextureContract.MaximumDiscoveredAssets)
        {
            candidates = null;
            problem = InventoryTooLargeProblem();
            return false;
        }

        if (discovered.Any(assetName => assetName is null))
        {
            candidates = null;
            problem = Problem(
                "textureInventoryFailed",
                "The installed canonical content inventory contains an invalid identity.");
            return false;
        }

        string[] ordered = discovered
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        IReadOnlyDictionary<string, int> collisions = ordered
            .GroupBy(StableIdentityNormalizer.Normalize, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var classified = new List<Candidate>(ordered.Length);
        long remainingInputBytes = ReviewTextureXnbClassifier.MaximumTotalInputBytes;
        foreach (string assetName in ordered)
        {
            string normalized = StableIdentityNormalizer.Normalize(assetName);
            if (!ReviewTextureContract.IsCanonicalAssetName(assetName)
                || normalized.Length == 0
                || collisions[normalized] != 1)
            {
                classified.Add(new Candidate(assetName, CandidateKind.Gap));
                continue;
            }
            if (remainingInputBytes == 0)
            {
                classified.Add(new Candidate(assetName, CandidateKind.Gap));
                continue;
            }

            bool probed;
            bool isTexture;
            long inputBytes;
            try
            {
                probed = source.TryClassifyTexture(
                    assetName,
                    remainingInputBytes,
                    out isTexture,
                    out inputBytes);
            }
            catch (Exception exception) when (IsControlledFailure(exception))
            {
                probed = false;
                isTexture = false;
                inputBytes = remainingInputBytes;
            }

            if ((probed && inputBytes <= 0)
                || inputBytes < 0
                || inputBytes > remainingInputBytes)
            {
                probed = false;
                isTexture = false;
                remainingInputBytes = 0;
            }
            else
            {
                remainingInputBytes -= inputBytes;
            }

            classified.Add(new Candidate(
                assetName,
                !probed
                    ? CandidateKind.Gap
                    : isTexture
                        ? CandidateKind.Texture
                        : CandidateKind.NonTexture));
        }

        candidates = classified.ToArray();
        problem = null;
        return true;
    }

    private static bool TryResolveTexture(
        string input,
        IReadOnlyList<Candidate> candidates,
        out string? assetName,
        out ReviewTextureProblem? problem)
    {
        string normalizedInput = StableIdentityNormalizer.Normalize(input);
        Candidate[] matches = candidates
            .Where(candidate => string.Equals(
                StableIdentityNormalizer.Normalize(candidate.AssetName),
                normalizedInput,
                StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        if (matches.Length > 1)
        {
            assetName = null;
            problem = Problem(
                "textureAssetAmbiguous",
                "The asset token collides after case and separator normalization; the query cannot proceed safely.");
            return false;
        }

        if (matches.Length == 0)
        {
            assetName = null;
            problem = Problem(
                "textureAssetUnknown",
                "The requested name is not a canonical installed content asset.");
            return false;
        }

        Candidate match = matches[0];
        assetName = match.AssetName;
        problem = match.Kind switch
        {
            CandidateKind.Texture => null,
            CandidateKind.NonTexture => Problem(
                "textureAssetNotTexture",
                "The requested canonical content asset is not a texture."),
            _ => Problem(
                "textureAssetUnclassified",
                "The requested canonical content asset could not be classified safely."),
        };
        return problem is null;
    }

    private static ReviewTexturePreviewReport? TryCreatePreview(
        IReviewTextureAsset texture,
        string runtimePath,
        string requestId,
        out ReviewTextureProblem? problem)
    {
        if (!string.Equals(texture.RuntimeFormat, "Color", StringComparison.Ordinal))
        {
            problem = Problem(
                "texturePreviewFormatUnsupported",
                "Preview is limited to final textures with the RGBA8 Color runtime format.");
            return null;
        }

        long sourcePixels = (long)texture.Width * texture.Height;
        if (texture.Width > ReviewTextureContract.MaximumSourceDimension
            || texture.Height > ReviewTextureContract.MaximumSourceDimension
            || sourcePixels > ReviewTextureContract.MaximumSourcePixels)
        {
            problem = Problem(
                "texturePreviewSourceTooLarge",
                $"Preview is limited to source textures no larger than {ReviewTextureContract.MaximumSourceDimension} pixels per dimension and {ReviewTextureContract.MaximumSourcePixels} total pixels.");
            return null;
        }

        (int width, int height) = ReviewTextureContract.PreviewDimensions(
            texture.Width,
            texture.Height);
        if ((long)width * height > ReviewTextureContract.MaximumPreviewPixels)
        {
            problem = Problem(
                "texturePreviewDimensionsInvalid",
                "The bounded preview dimensions are invalid.");
            return null;
        }

        string absoluteRuntimePath;
        string previewPath;
        FileStream? previewStream = null;
        var ownsPreview = false;
        try
        {
            absoluteRuntimePath = Path.GetFullPath(runtimePath);
            FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
            if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
                || (runtimeAttributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    "The review runtime preview root is not a regular directory.");
            }

            previewPath = ReviewTextureContract.PreviewPath(
                absoluteRuntimePath,
                requestId);
            previewStream = new FileStream(
                previewPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            ownsPreview = true;
            texture.WriteNearestNeighborPng(previewStream, width, height);
            previewStream.Flush(flushToDisk: true);
            FileAttributes previewAttributes = File.GetAttributes(previewPath);
            if ((previewAttributes & FileAttributes.ReparsePoint) != 0
                || (previewAttributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException(
                    "The review-texture preview is not a regular file.");
            }

            long encodedBytes = previewStream.Length;
            if (!ReviewTexturePngValidator.TryValidateRgba8(
                    previewStream,
                    ReviewTextureContract.MaximumPreviewBytes,
                    ReviewTextureContract.MaximumPreviewDimension,
                    ReviewTextureContract.MaximumPreviewPixels,
                    out ReviewTexturePngInfo? png)
                || png is null
                || png.Width != width
                || png.Height != height)
            {
                throw new InvalidDataException(
                    "The review-texture preview is not a complete matching RGBA PNG file.");
            }

            previewStream.Position = 0;
            using SHA256 sha256 = SHA256.Create();
            string hash = Convert.ToHexString(sha256.ComputeHash(previewStream))
                .ToLowerInvariant();
            previewStream.Dispose();
            previewStream = null;

            problem = null;
            return new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId),
                width,
                height,
                encodedBytes,
                hash);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            previewStream?.Dispose();
            previewStream = null;
            if (ownsPreview)
            {
                TryDeletePreview(runtimePath, requestId);
            }
            problem = Problem(
                "texturePreviewFailed",
                $"The bounded diagnostic PNG could not be created safely ({exception.GetType().Name}).");
            return null;
        }
        finally
        {
            previewStream?.Dispose();
        }
    }

    private static bool TryMetadata(
        IReviewTextureAsset texture,
        out ReviewTextureMetadataReport? metadata)
    {
        metadata = null;
        if (texture.Width <= 0
            || texture.Height <= 0
            || texture.LevelCount <= 0
            || string.IsNullOrWhiteSpace(texture.RuntimeFormat)
            || !ReviewTextureContract.IsBoundedText(
                texture.RuntimeFormat,
                ReviewTextureContract.MaximumRuntimeFormatLength))
        {
            return false;
        }

        metadata = new ReviewTextureMetadataReport(
            texture.Width,
            texture.Height,
            texture.RuntimeFormat,
            texture.LevelCount,
            texture.LevelCount > 1);
        return true;
    }

    private static ReviewTextureProblem? Validate(ReviewTextureQuery query)
    {
        if (query.Operation is not (
                ReviewTextureContract.AssetsOperation
                or ReviewTextureContract.GetOperation
                or ReviewTextureContract.PreviewOperation))
        {
            return Problem(
                "textureOperationUnknown",
                "The review-texture operation is unknown.");
        }

        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewTextureContract.MaximumPageLimit)
        {
            return Problem(
                "texturePaginationInvalid",
                $"Offset must be non-negative and limit must be between 1 and {ReviewTextureContract.MaximumPageLimit}.");
        }

        bool needsAsset = query.Operation is ReviewTextureContract.GetOperation
            or ReviewTextureContract.PreviewOperation;
        if (needsAsset
            && !ReviewTextureContract.IsCanonicalAssetName(query.Asset))
        {
            return Problem(
                "textureAssetInvalid",
                "A canonical bounded texture asset name is required.");
        }

        if (!needsAsset && query.Asset is not null)
        {
            return Problem(
                "textureRequestInvalid",
                "The review-texture request has unexpected operands.");
        }

        if (needsAsset
            && (query.Offset != 0 || query.Limit != 1))
        {
            return Problem(
                "textureRequestInvalid",
                "Exact texture operations do not accept pagination.");
        }

        return null;
    }

    private static ReviewTextureCoverageReport Coverage(
        IReadOnlyList<Candidate> candidates) =>
        new(
            candidates.Count,
            candidates.Count(candidate => candidate.Kind != CandidateKind.Gap),
            candidates.Count(candidate => candidate.Kind == CandidateKind.Texture),
            candidates.Count(candidate => candidate.Kind == CandidateKind.NonTexture),
            candidates.Count(candidate => candidate.Kind == CandidateKind.Gap));

    private static ReviewTexturePage Page(
        ReviewTextureQuery query,
        int returned,
        int total)
    {
        int consumed = Math.Min(total, checked(query.Offset + returned));
        return new ReviewTexturePage(
            query.Offset,
            query.Limit,
            returned,
            total,
            consumed < total ? consumed : null);
    }

    private static ReviewTextureProvenanceReport Provenance() =>
        new(
            ReviewTextureContract.FinalPipelineStage,
            false,
            ReviewTextureContract.ProvenanceUnavailableDetail);

    private static ReviewTextureProblem InventoryTooLargeProblem() =>
        Problem(
            "textureInventoryTooLarge",
            $"The installed canonical content inventory exceeds its bounded scan limits, including at most {ReviewTextureContract.MaximumDiscoveredAssets} candidates.");

    private static ReviewTextureProblem Problem(string code, string message) =>
        new(code, message);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException
            or PathTooLongException
            or SecurityException
            or UnauthorizedAccessException;
}

#if SDVKIT_GAME_AVAILABLE
internal sealed class StardewReviewTextureSource : IReviewTextureSource
{
    private readonly IModHelper _helper;
    private readonly string _contentRoot;
    private ReviewTextureXnbClassifier? _classifier;

    public StardewReviewTextureSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
        string gameRoot = Path.GetDirectoryName(typeof(Game1).Assembly.Location)
            ?? throw new InvalidOperationException("The game assembly has no directory.");
        _contentRoot = Path.Combine(gameRoot, "Content");
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public IReadOnlyList<string> DiscoverCanonicalAssetNames() =>
        ReviewTextureFileInventory.Discover(
            _contentRoot,
            assetName =>
                _helper.GameContent.ParseAssetName(assetName).LocaleCode is not null);

    public bool TryClassifyTexture(
        string assetName,
        long maximumInputBytes,
        out bool isTexture,
        out long inputBytes)
    {
        isTexture = false;
        inputBytes = maximumInputBytes;
        try
        {
            ReviewTextureXnbClassifier classifier = _classifier ??=
                new ReviewTextureXnbClassifier(
                    _contentRoot,
                    new ReviewTextureLzxReflectionDecoder(typeof(Texture2D).Assembly));
            return classifier.TryClassify(
                assetName,
                maximumInputBytes,
                out isTexture,
                out inputBytes);
        }
        catch (Exception exception) when (!ReviewException.IsFatal(exception))
        {
            isTexture = false;
            inputBytes = maximumInputBytes;
            return false;
        }
    }

    public IReviewTextureAsset LoadTexture(string assetName)
    {
        try
        {
            Texture2D texture = _helper.GameContent.Load<Texture2D>(assetName)
                ?? throw new InvalidDataException(
                    "The selected final texture is unavailable.");
            return new StardewReviewTextureAsset(texture);
        }
        catch (Exception exception) when (!ReviewException.IsFatal(exception))
        {
            throw new InvalidDataException(
                "The selected final texture could not be loaded.",
                exception);
        }
    }
}

internal sealed class StardewReviewTextureAsset(Texture2D texture) : IReviewTextureAsset
{
    private readonly Texture2D _texture = texture
        ?? throw new ArgumentNullException(nameof(texture));

    public int Width => _texture.Width;

    public int Height => _texture.Height;

    public string RuntimeFormat => _texture.Format.ToString();

    public int LevelCount => _texture.LevelCount;

    public void WriteNearestNeighborPng(Stream output, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_texture.Format != SurfaceFormat.Color)
        {
            throw new NotSupportedException(
                "The diagnostic texture preview requires the RGBA8 Color runtime format.");
        }
        if (width <= 0
            || height <= 0
            || width > ReviewTextureContract.MaximumPreviewDimension
            || height > ReviewTextureContract.MaximumPreviewDimension
            || (long)width * height > ReviewTextureContract.MaximumPreviewPixels
            || Width <= 0
            || Height <= 0
            || Width > ReviewTextureContract.MaximumSourceDimension
            || Height > ReviewTextureContract.MaximumSourceDimension
            || (long)Width * Height > ReviewTextureContract.MaximumSourcePixels)
        {
            throw new InvalidDataException(
                "The requested diagnostic texture dimensions are outside the fixed bounds.");
        }
        if (!output.CanWrite || !output.CanSeek || output.Length != 0)
        {
            throw new InvalidDataException(
                "The diagnostic texture output must be a new empty seekable stream.");
        }

        var sourcePixels = new Color[checked(Width * Height)];
        _texture.GetData(sourcePixels);
        var previewPixels = new Color[checked(width * height)];
        ReviewTextureSampling.CopyNearestNeighbor(
            sourcePixels,
            Width,
            Height,
            previewPixels,
            width,
            height);

        using var previewTexture = new Texture2D(
            _texture.GraphicsDevice,
            width,
            height,
            false,
            SurfaceFormat.Color);
        previewTexture.SetData(previewPixels);
        previewTexture.SaveAsPng(output, width, height);
    }
}

internal static class ReviewTextureCommand
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewTextureSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-texture runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-texture rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        ReviewTextureReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewTextureOperation.Failure(
                operation,
                source,
                new ReviewTextureProblem(
                    "textureReviewTopologyUnsupported",
                    "Review-texture queries require an active owned single project review."));
        }
        else if (!TryParse(
                arguments,
                out ReviewTextureQuery? query,
                out ReviewTextureProblem? problem))
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewTextureOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewTextureOperation.Execute(
                    query!,
                    source,
                    runtimePath,
                    requestId!);
            }
            catch (Exception exception) when (!ReviewException.IsFatal(exception))
            {
                report = ReviewTextureOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewTextureProblem(
                        "textureQueryFailed",
                        $"The review-texture query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewTextureResponseEnvelope(
            ReviewTextureContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-texture completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception) when (!ReviewException.IsFatal(exception))
        {
            if (report.Preview is not null)
            {
                ReviewTextureOperation.TryDeletePreview(runtimePath, requestId!);
            }

            monitor.Log(
                $"SDVKit review-texture could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewTextureQuery? query,
        out ReviewTextureProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count < 3
            || !string.Equals(arguments[0], "texture", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1]))
        {
            problem = new ReviewTextureProblem(
                "textureTransportInvalid",
                "The bounded review-texture transport request is invalid.");
            return false;
        }

        string operation = arguments[2];
        int expectedCount = operation switch
        {
            ReviewTextureContract.AssetsOperation => 5,
            ReviewTextureContract.GetOperation => 6,
            ReviewTextureContract.PreviewOperation => 6,
            _ => 0,
        };
        if (expectedCount == 0
            || arguments.Count != expectedCount
            || !int.TryParse(
                arguments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int offset)
            || !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit))
        {
            problem = new ReviewTextureProblem(
                "textureTransportInvalid",
                "The bounded review-texture transport request is invalid.");
            return false;
        }

        string? asset = null;
        if (operation is ReviewTextureContract.GetOperation
                or ReviewTextureContract.PreviewOperation
            && !ReviewTransportToken.TryDecode(
                arguments[5],
                ReviewTextureContract.MaximumAssetLength,
                out asset))
        {
            problem = new ReviewTextureProblem(
                "textureTransportInvalid",
                "The encoded review-texture asset name is invalid.");
            return false;
        }

        query = new ReviewTextureQuery(operation, asset, offset, limit);
        return true;
    }

    private static void WriteResponse(
        string runtimePath,
        ReviewTextureResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("The review runtime path is required.", nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string responsePath = ReviewTextureContract.ResponsePath(
            Path.GetFullPath(runtimePath),
            envelope.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ResponseJsonOptions);
        if (bytes.Length > ReviewTextureContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-texture response exceeds its maximum size.");
        }
        ReviewResponseFile.Write(responsePath, bytes);
    }
}
#endif

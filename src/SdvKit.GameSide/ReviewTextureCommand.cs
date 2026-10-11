using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.GameSide;

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
        ReviewResponseMailbox.Publish(responsePath, bytes);
    }
}

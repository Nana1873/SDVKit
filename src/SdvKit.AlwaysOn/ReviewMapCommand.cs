using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SdvKit.Cli.LiveLab;
#if SDVKIT_GAME_AVAILABLE
using StardewModdingAPI;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;
#endif

namespace SdvKit.AlwaysOn;

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewMapCommand
{
    private const string MissingToken = "-";
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewMapSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-map runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log("SDVKit review-map rejected an invalid request ID.", LogLevel.Error);
            return;
        }

        ReviewMapReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2 ? arguments[2] : "unknown";
            report = ReviewMapOperation.Failure(
                operation,
                source,
                new ReviewMapProblem(
                    "mapReviewTopologyUnsupported",
                    "Review-map queries require an active owned single project review."));
        }
        else if (!TryParse(arguments, out ReviewMapQuery? query, out ReviewMapProblem? problem))
        {
            string operation = arguments.Length > 2 ? arguments[2] : "unknown";
            report = ReviewMapOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewMapOperation.Execute(query!, source);
            }
            catch (Exception exception)
            {
                report = ReviewMapOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewMapProblem(
                        "mapQueryFailed",
                        $"The review-map query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewMapResponseEnvelope(
            ReviewMapContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-map completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit review-map could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewMapQuery? query,
        out ReviewMapProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count != 13
            || !string.Equals(arguments[0], "map", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1])
            || !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
            || !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
        {
            problem = new ReviewMapProblem(
                "mapTransportInvalid",
                "The bounded review-map transport request is invalid.");
            return false;
        }

        if (!TryDecodeOptional(arguments[5], ReviewMapContract.MaximumAssetLength, out string? asset)
            || !TryDecodeOptional(arguments[6], ReviewMapContract.MaximumIdentityLength, out string? layer)
            || !TryParseOptionalCoordinate(arguments[7], out int? x)
            || !TryParseOptionalCoordinate(arguments[8], out int? y)
            || !TryDecodeOptional(arguments[9], 32, out string? propertyScope)
            || !TryDecodeOptional(arguments[10], 32, out string? propertySource)
            || !TryParseOptionalCoordinate(arguments[11], out int? frameIndex)
            || !TryDecodeOptional(arguments[12], ReviewMapContract.MaximumPropertyNameLength, out string? property))
        {
            problem = new ReviewMapProblem(
                "mapTransportInvalid",
                "The encoded review-map operands are invalid.");
            return false;
        }

        query = new ReviewMapQuery(
            arguments[2],
            asset,
            layer,
            x,
            y,
            propertyScope,
            propertySource,
            frameIndex,
            property,
            offset,
            limit);
        return true;
    }

    private static bool TryDecodeOptional(string token, int maximumLength, out string? value)
    {
        if (string.Equals(token, MissingToken, StringComparison.Ordinal))
        {
            value = null;
            return true;
        }

        if (ReviewTransportToken.TryDecode(token, maximumLength, out string decoded))
        {
            value = decoded;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseOptionalCoordinate(string token, out int? value)
    {
        if (string.Equals(token, MissingToken, StringComparison.Ordinal))
        {
            value = null;
            return true;
        }

        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static void WriteResponse(string runtimePath, ReviewMapResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("The review runtime path is required.", nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string responsePath = ReviewMapContract.ResponsePath(
            Path.GetFullPath(runtimePath),
            envelope.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ResponseJsonOptions);
        if (bytes.Length > ReviewMapContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-map response exceeds its maximum size.");
        }
        ReviewResponseFile.Write(responsePath, bytes);
    }
}
#endif

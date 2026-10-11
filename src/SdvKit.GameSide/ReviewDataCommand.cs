using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.GameSide;

internal sealed class StardewReviewDataSource : IReviewDataSource
{
    private static readonly Regex LocaleSuffix = new(
        @"\.[a-z]{2}-[A-Z]{2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IModHelper _helper;
    private readonly string _contentRoot;
    private readonly string _dataRoot;

    public StardewReviewDataSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
        string gameRoot = Path.GetDirectoryName(typeof(Game1).Assembly.Location)
            ?? throw new InvalidOperationException("The game assembly has no directory.");
        _contentRoot = Path.Combine(gameRoot, "Content");
        _dataRoot = Path.Combine(_contentRoot, "Data");
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public IReadOnlyList<string> DiscoverCanonicalAssetNames()
    {
        var names = new List<string>();
        var pending = new Stack<string>();
        RefuseReparsePoint(_dataRoot);
        pending.Push(_dataRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The installed Data asset tree contains a reparse point.");
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
                    .GetRelativePath(_contentRoot, entry)
                    .Replace('\\', '/');
                string assetName = relative[..^Path.GetExtension(relative).Length];
                if (!LocaleSuffix.IsMatch(assetName))
                {
                    names.Add(assetName);
                }
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public object LoadAsset(string assetName) =>
        _helper.GameContent.Load<object>(assetName);

    private static void RefuseReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The installed Data asset root is not a regular directory.");
        }
    }
}

internal static class ReviewDataCommand
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewDataSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-data runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-data rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        ReviewDataReport report;
        string? role = Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE");
        role = string.IsNullOrWhiteSpace(role) ? null : role;
        bool selectedReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && (role is null || NetworkTwoContract.IsRole(role));
        if (!selectedReview)
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewDataOperation.Failure(
                operation,
                source,
                new ReviewDataProblem(
                    "dataReviewTopologyUnsupported",
                    "Review-data queries require an active owned project review role or local screen."));
        }
        else if (!TryParse(
                arguments,
                out ReviewDataQuery? query,
                out ReviewDataProblem? problem))
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewDataOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewDataOperation.Execute(query!, source);
            }
            catch (Exception exception)
            {
                report = ReviewDataOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewDataProblem(
                        "dataQueryFailed",
                        $"The review-data query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewDataResponseEnvelope(
            ReviewDataContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-data completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit review-data could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewDataQuery? query,
        out ReviewDataProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count < 3
            || !string.Equals(arguments[0], "data", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1]))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The bounded review-data transport request is invalid.");
            return false;
        }

        string operation = arguments[2];
        int expectedCount = operation switch
        {
            ReviewDataContract.AssetsOperation => 5,
            ReviewDataContract.KeysOperation => 6,
            ReviewDataContract.GetOperation => 7,
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
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The bounded review-data transport request is invalid.");
            return false;
        }

        string? asset = null;
        string? key = null;
        bool needsAsset = operation is ReviewDataContract.KeysOperation
            or ReviewDataContract.GetOperation;
        if (needsAsset
            && !ReviewTransportToken.TryDecode(
                arguments[5],
                ReviewDataContract.MaximumAssetLength,
                out asset))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The encoded review-data asset name is invalid.");
            return false;
        }

        if (operation == ReviewDataContract.GetOperation
            && !ReviewTransportToken.TryDecode(
                arguments[6],
                ReviewDataContract.MaximumKeyLength,
                out key))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The encoded review-data record key is invalid.");
            return false;
        }

        query = new ReviewDataQuery(operation, asset, key, offset, limit);
        return true;
    }

    private static void WriteResponse(
        string runtimePath,
        ReviewDataResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("The review runtime path is required.", nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string responsePath = ReviewDataContract.ResponsePath(
            Path.GetFullPath(runtimePath),
            envelope.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ResponseJsonOptions);
        if (bytes.Length > ReviewDataContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-data response exceeds its maximum size.");
        }
        ReviewResponseMailbox.Publish(responsePath, bytes);
    }
}

using System.Text.RegularExpressions;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record ReviewLogDiagnostic(string Time, string Severity, string Attribution,
    string Phase, IReadOnlyList<string> Lines, int WithheldLines, bool Truncated);
internal sealed record ReviewLogCounts(int Total, int Matching, int Returned, bool TotalIsExact);
internal sealed record ReviewLogSource(string Name, long TotalBytes, int ScannedBytes,
    bool ScanTruncated, bool IncompleteLineWithheld, DateTimeOffset LastWrittenAtUtc);
internal sealed record ReviewLogDiagnosticsResult(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, string ModId, string? BuildIdentity,
    ReviewLogSource? Source, ReviewLogCounts? Counts, int Limit, bool Truncated,
    IReadOnlyList<ReviewLogDiagnostic> Diagnostics);

internal static class ProjectReviewLogDiagnostics
{
    internal const int DefaultLimit = 20;
    internal const int MaximumLimit = 100;
    private const int MaximumLines = 32;
    private const int MaximumLineLength = 1024;
    private static readonly Regex Secret = Pattern(
        "(?i)(?:\\b[\\w.-]*(?:token|password|passwd|secret|api[_-]?key|authorization|cookie|connectionstring)[\\w.-]*\\b[\"']?\\s*[:=]|\\b(?:PID|ProcessId)\\s*[:=]|\\bBearer\\s+|\\b(?:gh[pousr]_|github_pat_|sk-)[A-Za-z0-9_-]+|://[^\\s/]+@)");
    private static readonly Regex AbsolutePath = Pattern(
        "(?:[A-Za-z]:[\\\\/]|\\\\\\\\|(?<![A-Za-z0-9_./])/(?:[A-Za-z0-9_.-]+/))[^\\r\\n\"'<>|]*");
    private static readonly Regex ExceptionLine = Pattern(
        @"^\s*(?:at |---> |--- End of |(?:[\w`.+]+\.)?[\w`+]*Exception\b|(?:[\w`.+]+\.)?[\w`+]*Error\b)");

    internal static bool ValidQuery(string? modId, int limit) => modId is { Length: > 0 and <= 256 }
        && modId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
        && limit is >= 1 and <= MaximumLimit;

    internal static ReviewLogDiagnosticsResult Execute(ProjectReviewMcpRuntimeReader reader,
        string modId, int limit = DefaultLimit)
    {
        ReviewLogDiagnosticsResult Failure(string code) => new(1, "unavailable", code, null,
            reader.Topology, reader.Role, modId, null, null, null, limit, false, []);
        if (!ValidQuery(modId, limit))
        {
            return Failure("reviewDiagnosticsArgumentsInvalid");
        }
        ProjectReviewMcpContextResult verified = reader.ReadContext();
        if (!verified.Succeeded)
        {
            return Failure(verified.ErrorCode!);
        }
        ProjectReviewMcpVerifiedContext context = verified.Context!;
        ProjectReviewOwnedArtifact? selected = context.Staging.Artifacts.SingleOrDefault(a =>
            string.Equals(a.Manifest.UniqueId, modId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return Failure("reviewModNotSelected");
        }
        try
        {
            OwnedReviewLog log = OwnedReviewLogReader.Read(reader, context);
            (ReviewLogDiagnostic[] diagnostics, int total, int matching) = Parse(log.Text,
                selected.Manifest, context.Staging.Artifacts.Select(a => a.Manifest).ToArray(), limit);
            return new(1, "ready", null, context.State.LaunchId, reader.Topology, reader.Role,
                selected.Manifest.UniqueId, selected.BuildIdentity,
                new ReviewLogSource("isolatedSmapiLatest", log.TotalBytes, log.ScannedBytes,
                    log.ScanTruncated, log.IncompleteLineWithheld, log.LastWrittenAtUtc),
                new ReviewLogCounts(total, matching, diagnostics.Length,
                    !log.ScanTruncated && !log.IncompleteLineWithheld), limit,
                log.ScanTruncated || log.IncompleteLineWithheld || matching > diagnostics.Length
                    || diagnostics.Any(d => d.Truncated), diagnostics);
        }
        catch (InvalidDataException exception)
        {
            return Failure(exception.Message is "reviewLogPathInvalid" or "reviewLogStale"
                or "reviewLogIdentityMismatch" or "reviewLogChanged" or "reviewLogBindingChanged"
                ? exception.Message : "reviewLogInvalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException
            or RegexMatchTimeoutException)
        {
            return Failure("reviewLogUnavailable");
        }
    }

    internal static (ReviewLogDiagnostic[] Diagnostics, int Total, int Matching) Parse(string text,
        ProjectReviewManifest selected, IReadOnlyList<ProjectReviewManifest> staged, int limit)
    {
        var returned = new Queue<ReviewLogDiagnostic>();
        var lines = new List<string>();
        Match? header = null;
        int total = 0, matching = 0;
        string phase = "unknown";
        string entryPhase = phase;
        void Complete()
        {
            if (header is null || header.Groups["level"].Value is not ("WARN" or "ERROR" or "ALERT"))
            {
                return;
            }
            string logger = header.Groups["logger"].Value;
            bool named = string.Equals(logger, selected.Name, StringComparison.OrdinalIgnoreCase);
            bool mentioned = lines.Any(line => Mentions(line, selected.UniqueId) || Mentions(line, selected.Name));
            bool shared = logger == "SMAPI" || staged.Any(a => string.Equals(a.UniqueId, selected.ContentPackFor, StringComparison.OrdinalIgnoreCase) && a.Name == logger);
            if (!named && !(shared && mentioned))
            {
                return;
            }
            matching++;
            string attribution = named
                ? staged.Count(a => string.Equals(a.Name, selected.Name, StringComparison.OrdinalIgnoreCase)) == 1
                    && logger is not ("SMAPI" or "SDVKit AlwaysOn") ? "logger" : "ambiguousLogger"
                : "sharedMention";
            var visible = new List<string>();
            int withheld = 0;
            bool truncated = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                bool otherMod = staged.Any(a => a.UniqueId != selected.UniqueId
                    && (Mentions(line, a.UniqueId) || (a.Name != selected.Name && Mentions(line, a.Name))));
                if (otherMod || Secret.IsMatch(line)
                    || (i > 0 && !ExceptionLine.IsMatch(line)
                        && !Mentions(line, selected.UniqueId) && !Mentions(line, selected.Name)))
                {
                    withheld++;
                    continue;
                }
                string safe = AbsolutePath.Replace(line, "[private path withheld]");
                if (safe != line)
                {
                    withheld++;
                }
                safe = new string(safe.Where(c => !char.IsControl(c) || c == '\t').ToArray());
                if (safe.Length > MaximumLineLength)
                {
                    safe = safe[..MaximumLineLength];
                    truncated = true;
                }
                if (visible.Count < MaximumLines)
                {
                    visible.Add(safe);
                }
                else
                {
                    truncated = true;
                }
            }
            if (visible.Count == 0)
            {
                visible.Add("[message context withheld]");
            }
            returned.Enqueue(new(header.Groups["time"].Value, header.Groups["level"].Value,
                attribution, entryPhase, visible, withheld, truncated));
            if (returned.Count > limit)
            {
                returned.Dequeue();
            }
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match next = OwnedReviewLogReader.Header.Match(line);
            if (next.Success)
            {
                Complete();
                total++;
                header = next;
                lines.Clear();
                string message = next.Groups["message"].Value;
                if (next.Groups["logger"].Value == "SMAPI"
                    && message.StartsWith("Loading mods", StringComparison.Ordinal))
                {
                    phase = "loading";
                }
                if (next.Groups["logger"].Value == "SMAPI"
                    && message.StartsWith("Mods loaded and ready!", StringComparison.Ordinal))
                {
                    phase = "runtime";
                }
                entryPhase = phase;
                lines.Add(message);
            }
            else if (header is not null && line.Length > 0)
            {
                lines.Add(line);
            }
        }
        Complete();
        return (returned.ToArray(), total, matching);
    }

    internal static string? DiscloseLine(string line, out bool withheld, bool cpTokenMetadata = false)
    {
        string checkedLine = cpTokenMetadata ? Regex.Replace(line,
            @"\b(?:invalid tokens?|unready tokens|unavailable mod tokens|tokens used|has tokens|Local tokens):", "CP symbols:",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : line;
        withheld = Secret.IsMatch(checkedLine);
        if (withheld) return null;
        string safe = AbsolutePath.Replace(line, "[private path withheld]");
        withheld = safe != line;
        return new string(safe.Where(c => !char.IsControl(c) || c == '\t').ToArray());
    }

    internal static bool Mentions(string text, string value) => value.Length > 0
        && Regex.IsMatch(text, @"(?<![A-Za-z0-9_.-])" + Regex.Escape(value) + @"(?![A-Za-z0-9_.-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

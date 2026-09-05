using System.Diagnostics;
using System.Text.RegularExpressions;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record CpPatchState(bool LoadedAndEnabled, bool ConditionsMatch, bool Applied, string Details);
internal sealed record CpResponse(string State, string? ErrorCode, bool CommandWritten,
    bool CommandMayHaveBeenWritten, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    string? LogTime, IReadOnlyList<string> Messages, int WithheldLines, bool Truncated,
    IReadOnlyList<CpPatchState> Patches);
internal sealed record CpDiagnosisResult(string State, string? ErrorCode, string? LaunchId,
    string PackId, string ProviderId, string? ProviderVersion, string? PackBuildIdentity, string? ProviderBuildIdentity,
    bool PackLoaded, bool ProviderLoaded, CpResponse? Summary, CpResponse? Parse,
    string AssetObservation = "notRequested; inspect separately after diagnosis; inspection may load the asset")
{
    public CpResponse? Reload { get; init; }
}

internal static class ProjectReviewCpDiagnosis
{
    internal const string ProviderId = "Pathoschild.ContentPatcher";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] ReservedPackIds = ["asset", "full", "unsorted", "compact"];
    private static readonly Regex SensitiveToken = new(
        @"(?i)(?:api[_-]?key|password|passwd|secret|authorization|cookie|token|connectionstring)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PatchRow = new(
        @"^\s*\[(?<loaded>X| )\]\s*\|\s*\[(?<conditions>X| )\]\s*\|\s*\[(?<applied>X| )\]\s*\|(?<details>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // SMAPI splits quoted arguments. Reject quotes/escapes/control characters instead
    // of trying to escape them; summary keywords are reserved even when quoted.
    internal static bool ValidArguments(string pack, string provider, string? asset, string? parse) =>
        ProjectReviewLogDiagnostics.ValidQuery(pack, 1)
        && !ReservedPackIds.Contains(pack, StringComparer.OrdinalIgnoreCase)
        && string.Equals(provider, ProviderId, StringComparison.OrdinalIgnoreCase)
        && (asset is null || SafeArgument(asset, 256) && asset.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or ' ')
            && asset.Split('/').All(s => s.Length > 0 && s == s.Trim()))
        && (parse is null || SafeArgument(parse, 512));

    private static bool SafeArgument(string value, int maximum) => value.Length > 0 && value.Length <= maximum
        && value == value.Trim() && value.All(c => !char.IsControl(c) && c is not ('"' or '\\' or ';'));

    internal static CpDiagnosisResult Execute(ProjectReviewMcpRuntimeReader reader, string packId,
        string providerId, string? asset, string? parse,
        Func<string, LiveLabCommandResult>? send = null, TimeSpan? timeout = null,
        ProjectReviewActionLock? heldActionLock = null, bool reload = false)
    {
        ProjectReviewMcpVerifiedContext? context = null;
        ProjectReviewOwnedArtifact? pack = null, provider = null;
        bool packLoaded = false, providerLoaded = false;
        CpResponse? summary = null, parsed = null, reloaded = null;
        CpDiagnosisResult Result(string state, string? code) => new(state, code, context?.State.LaunchId,
            pack?.Manifest.UniqueId ?? packId, provider?.Manifest.UniqueId ?? providerId,
            provider?.Manifest.Version, pack?.StagedBuildIdentity, provider?.StagedBuildIdentity, packLoaded, providerLoaded, summary, parsed)
        { Reload = reloaded };
        if (reader.Topology != LiveLabState.SingleTopology || !ValidArguments(packId, providerId, asset, parse))
            return Result("unavailable", "cpArgumentsInvalid");
        try
        {
            var verified = reader.ReadContext();
            if (!verified.Succeeded) return Result("unavailable", verified.ErrorCode);
            context = verified.Context!;
            pack = context.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(packId, StringComparison.OrdinalIgnoreCase));
            provider = context.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (pack is null || provider is null || !string.Equals(pack.Manifest.ContentPackFor, providerId, StringComparison.OrdinalIgnoreCase))
                return Result("unavailable", "cpSelectionMismatch");
            if (context.Staging.Artifacts.Count(a => a.Manifest.Name.Equals(provider.Manifest.Name, StringComparison.OrdinalIgnoreCase)) != 1
                || provider.Manifest.Name is "SMAPI" or "SDVKit AlwaysOn")
                return Result("unavailable", "cpProviderLoggerAmbiguous");
            var loaded = context.AlwaysOn.LoadedMods;
            bool IsLoaded(ProjectReviewOwnedArtifact a, bool isPack) => loaded?.State == "ready"
                && loaded.Mods.Any(m => m.UniqueId.Equals(a.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase)
                    && m.Version == ProjectModLaunchState.NormalizeVersion(a.Manifest.Version) && m.IsContentPack == isPack);
            packLoaded = IsLoaded(pack, true);
            providerLoaded = IsLoaded(provider, false);
            if (!packLoaded || !providerLoaded || !context.AllTargetsReady)
                return Result("unavailable", "cpSelectedModsNotLoaded");
            // Only the installed version whose official source and live output were verified.
            if (provider.Manifest.Version != "2.9.1") return Result("unsupported", "cpVersionUnsupported");
            string runtimePath = LiveLabPaths.Resolve(reader.ProjectRoot).RuntimePath;
            heldActionLock?.RequireHeldFor(runtimePath);
            using var actionLock = heldActionLock is null ? ProjectReviewActionLock.TryAcquire(runtimePath) : null;
            if (actionLock is null && heldActionLock is null) return Result("unavailable", "reviewBusy");
            send ??= command => ProjectReviewService.ExecuteCommand(command, reader.Topology, null, reader.ProjectRoot);
            string selectedId = pack.Manifest.UniqueId;
            if (reload)
            {
                if (heldActionLock is null || reader.HeldOperationLock is null)
                    return Result("unavailable", "cpRefreshLockRequired");
                reloaded = Capture($"patch reload \"{selectedId}\"", false, isReload: true);
                if (reloaded.State != "ready") return Result("incomplete", reloaded.ErrorCode);
            }
            summary = Capture($"patch summary \"{selectedId}\"" + (asset is null ? "" : $" asset \"{asset}\""), false);
            if (summary.State != "ready") return Result("incomplete", summary.ErrorCode);
            if (parse is not null)
            {
                parsed = Capture($"patch parse \"{parse}\" \"{selectedId}\"", true);
                if (parsed.State != "ready") return Result("incomplete", parsed.ErrorCode);
            }
            return Result("ready", null);

            CpResponse Capture(string command, bool isParse, bool isReload = false)
            {
                DateTimeOffset started = DateTimeOffset.UtcNow;
                bool written = false, mayHaveWritten = false;
                CpResponse Failure(string code) => new("incomplete", code, written, mayHaveWritten,
                    started, DateTimeOffset.UtcNow, null, [], 0, false, []);
                try
                {
                    OwnedReviewLog before = OwnedReviewLogReader.Read(reader, context);
                    if (before.ScanTruncated || before.IncompleteLineWithheld) return Failure("cpLogWindowUnavailable");
                    string nonce = "sdvkit-" + Guid.NewGuid().ToString("N");
                    string begin = nonce + "-begin", end = nonce + "-end";
                    var timer = Stopwatch.StartNew();
                    bool Dispatch(string line, bool diagnosis)
                    {
                        var delivered = send(line);
                        var report = delivered.Report as ProjectReviewCommandReport;
                        if (diagnosis)
                        {
                            written = report?.CommandWritten == true;
                            mayHaveWritten = report?.CommandWritten != false;
                        }
                        return delivered.ExitCode == 0 && report?.CommandWritten == true;
                    }
                    string Delta()
                    {
                        var after = OwnedReviewLogReader.Read(reader, context);
                        return WindowDelta(before, after);
                    }
                    bool WaitFor(Func<string, bool> predicate)
                    {
                        while (timer.Elapsed < (timeout ?? Timeout))
                        {
                            if (predicate(Delta())) return true;
                            Thread.Sleep(50);
                        }
                        return false;
                    }
                    if (!Dispatch($"patch parse \"{begin}\" \"{selectedId}\" compact", false)) return Failure("cpMarkerDeliveryFailed");
                    if (!WaitFor(t => Entries(t, provider.Manifest.Name).Any(e => e.Text.Trim() == Marker(begin)))) return Failure("cpResponseTimedOut");
                    if (!Dispatch(command, true)) return Failure("cpCommandDeliveryFailed");
                    if (!WaitFor(t => Entries(t, provider.Manifest.Name).Count >= 2)) return Failure("cpResponseTimedOut");
                    if (!Dispatch($"patch parse \"{end}\" \"{selectedId}\" compact", false)) return Failure("cpMarkerDeliveryFailed");
                    if (!WaitFor(t => Entries(t, provider.Manifest.Name).Any(e => e.Text.Trim() == Marker(end)))) return Failure("cpResponseTimedOut");
                    return InterpretWindow(Delta(), provider.Manifest.Name, selectedId, asset, parse,
                        begin, end, isParse, context.Staging.Artifacts.Select(a => a.Manifest).ToArray(), started, isReload);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException or RegexMatchTimeoutException)
                {
                    return Failure("cpLogWindowUnavailable");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException or RegexMatchTimeoutException)
        {
            return Result("unavailable", "cpReviewUnavailable");
        }
    }

    internal static string WindowDelta(OwnedReviewLog before, OwnedReviewLog after)
    {
        if (after.FileIdentity != before.FileIdentity || after.ScanTruncated
            || after.TotalBytes < before.TotalBytes || !after.Text.StartsWith(before.Text, StringComparison.Ordinal))
            throw new InvalidDataException("cpLogWindowChanged");
        return after.Text[before.Text.Length..];
    }

    private static string Marker(string value) => $"The token string is valid and ready. Parsed value: \"{value}\"";
    private sealed record Entry(string Time, string Level, string Text, bool Interrupted = false);
    private static List<Entry> Entries(string text, string provider)
    {
        var result = new List<Entry>();
        string? time = null, level = null;
        var lines = new List<string>();
        void Finish()
        {
            if (time is not null) result.Add(new(time, level!, string.Join('\n', lines)));
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var header = OwnedReviewLogReader.Header.Match(line);
            if (header.Success)
            {
                Finish();
                time = header.Groups["logger"].Value == provider ? header.Groups["time"].Value : null;
                level = header.Groups["level"].Value;
                lines.Clear();
                lines.Add(header.Groups["message"].Value);
            }
            else if (time is not null) lines.Add(line);
            else if (!string.IsNullOrWhiteSpace(line) && result.Count > 0)
            {
                // A foreign logger followed by continuation text can hide the
                // remainder of a CP reply. Do not interpret that partial entry.
                result[^1] = result[^1] with { Interrupted = true };
            }
        }
        Finish();
        return result;
    }

    internal static CpResponse InterpretWindow(string text, string provider, string packId,
        string? asset, string? parse, string begin, string end, bool isParse,
        IReadOnlyList<ProjectReviewManifest> staged, DateTimeOffset started, bool isReload = false)
    {
        CpResponse Failure(string code) => new("incomplete", code, true, true, started,
            DateTimeOffset.UtcNow, null, [], 0, false, []);
        var entries = Entries(text, provider);
        // CP 2.9.1 invalidates affected assets during reload. Its one known trace
        // can precede SMAPI's multiline propagation log; neither is the reply.
        // Only the subsequent complete INFO acknowledgment establishes reload.
        if (isReload && entries.Count == 4 && entries[1].Level == "TRACE"
            && entries[1].Text.Trim() == "Requested cache invalidation for all assets matching a predicate.")
            entries.RemoveAt(1);
        if (entries.Count != 3 || entries[0].Text.Trim() != Marker(begin) || entries[2].Text.Trim() != Marker(end)
            || entries[0].Level != "DEBUG" || entries[2].Level != "DEBUG" || entries.Take(2).Any(e => e.Interrupted))
            return Failure("cpResponseUncorrelatedOrOverlapping");
        Entry response = entries[1];
        string message = response.Text;
        bool known;
        if (isReload)
        {
            known = response.Level == "INFO" && message.Trim() == "Content pack reloaded.";
        }
        else if (isParse)
        {
            known = response.Level == "ERROR" && message.TrimStart().StartsWith("Can't parse that token value:", StringComparison.Ordinal)
                || response.Level == "DEBUG" && message.Contains("Metadata\n", StringComparison.Ordinal)
                && message.Contains($"   raw value:   {parse}\n", StringComparison.Ordinal)
                && message.Contains("Diagnostic state\n", StringComparison.Ordinal) && message.Contains("Result\n", StringComparison.Ordinal)
                && (message.TrimEnd().EndsWith("The token string is invalid or unready.", StringComparison.Ordinal)
                    || message.TrimEnd().Split('\n')[^1].TrimStart().StartsWith("The token string is valid and ready. Parsed value: \"", StringComparison.Ordinal)
                        && message.TrimEnd().EndsWith('"'));
        }
        else
        {
            int section = message.IndexOf("== Content patches ==", StringComparison.Ordinal);
            known = response.Level == "DEBUG" && section >= 0;
            message = section >= 0 ? message[section..] : message;
            known &= message.Contains($"(Filtered to content pack ID: {packId}.)", StringComparison.Ordinal)
                && (asset is null || message.Contains($"(Filtered to asset name: {asset}.)", StringComparison.Ordinal));
            if (message.Contains("   Patches:", StringComparison.Ordinal))
                known &= message.Contains("loaded  | conditions | applied |", StringComparison.Ordinal)
                    && (message.Contains("   Current changes:\n", StringComparison.Ordinal) || message.Contains("   No current changes.", StringComparison.Ordinal));
        }
        if (!known) return Failure("cpOutputUnsupported");
        if (isParse && (SensitiveToken.IsMatch(parse ?? "") || message.Split('\n').Any(line =>
                line.TrimStart().StartsWith("tokens used:", StringComparison.Ordinal) && SensitiveToken.IsMatch(line[(line.IndexOf(':') + 1)..]))))
            return new("incomplete", "cpParsePrivateContextWithheld", true, true, started,
                DateTimeOffset.UtcNow, response.Time, ["[sensitive token context and result withheld]"], message.Split('\n').Length, false, []);
        var visible = new List<string>();
        var patches = new List<CpPatchState>();
        int withheld = 0;
        bool truncated = false;
        foreach (string line in message.Split('\n'))
        {
            int separator = line.IndexOf('|');
            bool sensitiveTable = separator >= 0 && line[(separator + 1)..].TrimStart().StartsWith('[')
                && SensitiveToken.IsMatch(line[..separator]);
            bool unrelated = staged.Any(m => !m.UniqueId.Equals(packId, StringComparison.OrdinalIgnoreCase)
                && !m.UniqueId.Equals(ProviderId, StringComparison.OrdinalIgnoreCase)
                && (ProjectReviewLogDiagnostics.Mentions(line, m.UniqueId) || ProjectReviewLogDiagnostics.Mentions(line, m.Name)));
            string? safe = ProjectReviewLogDiagnostics.DiscloseLine(line, out bool privateContext, cpTokenMetadata: true);
            if (unrelated || privateContext || sensitiveTable) withheld++;
            if (unrelated || safe is null || sensitiveTable) continue;
            if (safe.Length > 1024) { safe = safe[..1024]; truncated = true; }
            if (visible.Count >= 256) { truncated = true; continue; }
            visible.Add(safe);
            if (!isParse)
            {
                var row = PatchRow.Match(safe);
                if (row.Success) patches.Add(new(row.Groups["loaded"].Value == "X", row.Groups["conditions"].Value == "X",
                    row.Groups["applied"].Value == "X", row.Groups["details"].Value.Trim()));
            }
        }
        return new("ready", null, true, true, started, DateTimeOffset.UtcNow, response.Time,
            visible, withheld, truncated, patches);
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record ConfigReconcileResult(string State, string? ErrorCode, string Recovery,
    string? LaunchId, OwnedProcessIdentity? Process, string? UniqueId, string? LaunchBuildIdentity,
    ConfigReconcileReceipt? Reconciliation, double ElapsedSeconds)
{
    public string? AuditWarning { get; init; }
}

internal static class ProjectReviewConfigReconcile
{
    internal const int MaximumConfigBytes = 1024 * 1024;
    private const string Recovery = "Inspect the retained configuration audit, then use project review stop and reset. Start the exact selection again if ownership or configuration cannot be confirmed.";
    private const string AuditRecovery = "The configuration is accepted by the owned review, but its retained audit could not be confirmed. Retain this result and run the same config-reconcile command again to verify the export and finalize the audit.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonDocumentOptions ConfigJsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static ReviewConfigBaseline CaptureBaseline(string stagingPath, string expectedIdentity)
    {
        string config = Path.Combine(stagingPath, "config.json");
        string? configHash = File.Exists(config) ? ModBuildIdentity.ComputeFile(config) : null;
        var identities = ModBuildIdentity.ComputeConfigurationIdentities(stagingPath);
        if (identities.FullIdentity != expectedIdentity
            || configHash != (File.Exists(config) ? ModBuildIdentity.ComputeFile(config) : null))
            throw new InvalidDataException("The staged configuration changed before its baseline was retained.");
        return new(identities.ContentIdentity, configHash);
    }

    internal static bool MatchesSelectedContent(ProjectReviewOwnedArtifact artifact, string path) =>
        artifact.CpRefresh is null && artifact.ConfigBaseline is { ConfigHash: not null } baseline
        && ModBuildIdentity.ComputeConfigurationIdentities(path).ContentIdentity == baseline.ContentIdentity;

    internal static bool ValidConfigurationOwnership(ProjectReviewOwnedArtifact artifact, LiveLabPaths paths, string topology)
    {
        ReviewConfigBaseline? baseline = artifact.ConfigBaseline;
        if (baseline is not null && (topology != LiveLabState.SingleTopology
            || !ModBuildIdentity.IsValid(baseline.ContentIdentity)
            || baseline.ConfigHash is not null && !ModBuildIdentity.IsValid(baseline.ConfigHash))) return false;
        if (artifact.ConfigReconciliation is not { } receipt) return true;
        return topology == LiveLabState.SingleTopology && artifact.CpRefresh is null
            && baseline?.ConfigHash is not null && receipt.ContentIdentity == baseline.ContentIdentity
            && Guid.TryParseExact(receipt.ReconciliationId, "N", out _)
            && Guid.TryParseExact(receipt.LaunchId, "N", out _)
            && (receipt.PreviousReconciliationId is null
                ? receipt.PreviousConfigHash == baseline.ConfigHash
                : Guid.TryParseExact(receipt.PreviousReconciliationId, "N", out _)
                    && receipt.PreviousReconciliationId != receipt.ReconciliationId)
            && ModBuildIdentity.IsValid(receipt.PreviousConfigHash) && ModBuildIdentity.IsValid(receipt.ConfigHash)
            && ModBuildIdentity.IsValid(receipt.StagedBuildIdentity) && receipt.ReconciledAtUtc != default
            && receipt.AuditPath == EvidencePath(paths, receipt.LaunchId, receipt.ReconciliationId, "receipt.json")
            && receipt.ExportPath == EvidencePath(paths, receipt.LaunchId, receipt.ReconciliationId, "config.json");
    }

    internal static ConfigReconcileResult Execute(string labRoot, string uniqueId,
        ILabProcessHost? processHost = null, Func<DateTimeOffset>? utcNow = null,
        Action<string, ProjectReviewStaging>? writeOwnership = null,
        Action<string>? beforeAuditWrite = null)
    {
        var timer = Stopwatch.StartNew();
        ProjectReviewMcpVerifiedContext? context = null;
        ProjectReviewOwnedArtifact? selected = null;
        ConfigReconcileReceipt? receipt = null;
        string? auditPath = null;
        string? auditWarning = null;
        bool commitAttempted = false;
        ConfigReconcileResult Result(string state, string? error) => new(state, error,
            auditWarning is not null ? AuditRecovery : commitAttempted && state != "reconciled" ? Recovery : "none", context?.State.LaunchId,
            context?.State.OwnedProcessIdentity, selected?.Manifest.UniqueId, selected?.BuildIdentity,
            receipt, timer.Elapsed.TotalSeconds)
        { AuditWarning = auditWarning };
        if (string.IsNullOrWhiteSpace(uniqueId) || uniqueId.Length > 256 || uniqueId.Any(char.IsControl)
            || !ReviewTransportText.IsWellFormedUtf16(uniqueId)) return Result("rejected", "configReconcileArgumentsInvalid");
        try
        {
            LiveLabPaths paths = LiveLabPaths.Resolve(labRoot);
            using var operationLock = LiveLabOperationLock.TryAcquire(labRoot);
            if (operationLock is null) return Result("rejected", "reviewBusy");
            using var actionLock = ProjectReviewActionLock.TryAcquire(paths.RuntimePath);
            if (actionLock is null) return Result("rejected", "reviewBusy");

            // This structural read identifies the requested artifact only. Runtime and
            // all unselected content are still verified before any export or commit.
            var preliminary = ProjectModStager.ReadReviewForCleanup(paths, LiveLabState.SingleTopology);
            if (preliminary.Problem is not null || preliminary.Staging is null)
                return Result("rejected", preliminary.Problem?.Code ?? "reviewOwnershipMissing");
            selected = preliminary.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
            if (selected is null || selected.Manifest.Kind != ProjectInspectionReport.SmapiMod
                && !string.Equals(selected.Manifest.ContentPackFor, ProjectReviewCpDiagnosis.ProviderId, StringComparison.OrdinalIgnoreCase))
                return Result("rejected", "configReconcileSelectionMismatch");
            if (selected.ConfigBaseline?.ConfigHash is null)
                return Result("rejected", "configReconcileBaselineMissing");
            if (preliminary.Staging.Artifacts.Any(a => a.CpRefresh is not null))
                return Result("rejected", "configReconcileAfterCpRefreshUnsupported");
            RequirePlain(preliminary.Staging.OwnershipPath);
            using (OwnedReviewLogReader.OpenSingleLinkSnapshot(preliminary.Staging.OwnershipPath)) { }

            var reader = new ProjectReviewMcpRuntimeReader(labRoot, processHost, utcNow)
            { HeldOperationLock = operationLock, ReconcileConfigUniqueId = selected.Manifest.UniqueId };
            var verified = reader.ReadContext();
            if (!verified.Succeeded) return Result("rejected", verified.ErrorCode);
            context = verified.Context!;
            selected = context.Staging.Artifacts.Single(a => a.Manifest.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
            if (!context.AllTargetsReady || context.AlwaysOn.LoadedMods?.State != "ready"
                || !context.AlwaysOn.LoadedMods.Mods.Any(mod => mod.UniqueId.Equals(selected.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase)
                    && mod.Version == ProjectModLaunchState.NormalizeVersion(selected.Manifest.Version)
                    && mod.IsContentPack == (selected.Manifest.Kind == ProjectInspectionReport.ContentPack)))
                return Result("rejected", "configReconcileSelectedModNotLoaded");

            string configPath = Path.Combine(selected.StagingPath, "config.json");
            if (!File.Exists(configPath)) return Result("rejected", "configReconcileConfigMissing");
            RequirePlain(configPath);
            // The exact regular single-link config stays open without write/delete
            // sharing until the audit and ownership update have both completed.
            using var config = OwnedReviewLogReader.OpenSingleLinkSnapshot(configPath);
            if (config.Length > MaximumConfigBytes) return Result("rejected", "configReconcileConfigTooLarge");
            byte[] bytes = new byte[(int)config.Length];
            config.ReadExactly(bytes);
            try
            {
                using var json = JsonDocument.Parse(bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes.AsMemory(3) : bytes, ConfigJsonOptions);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return Result("rejected", "configReconcileInvalidJson");
            }
            catch (JsonException) { return Result("rejected", "configReconcileInvalidJson"); }
            string hash = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
            string previousHash = selected.ConfigReconciliation?.ConfigHash ?? selected.ConfigBaseline!.ConfigHash!;
            RequirePlainReview(context.Staging);
            var identities = ModBuildIdentity.ComputeConfigurationIdentities(selected.StagingPath);
            if (identities.ContentIdentity != selected.ConfigBaseline!.ContentIdentity)
                return Result("rejected", "reviewStagingOwnershipDrifted");
            if (hash == previousHash)
            {
                receipt = selected.ConfigReconciliation;
                var unchanged = reader.ReadContext();
                if (!unchanged.Succeeded || !SameBinding(context, unchanged.Context!))
                    return Result("rejected", "configReconcileBindingChanged");
                if (receipt is not null)
                {
                    auditPath = Path.GetFullPath(Path.Combine(paths.ProjectRoot, receipt.AuditPath));
                    try
                    {
                        string retainedExport = Path.GetFullPath(Path.Combine(paths.ProjectRoot, receipt.ExportPath));
                        RequirePlain(retainedExport);
                        using var export = OwnedReviewLogReader.OpenSingleLinkSnapshot(retainedExport);
                        if ($"sha256:{Convert.ToHexString(SHA256.HashData(export)).ToLowerInvariant()}" != receipt.ConfigHash)
                            throw new InvalidDataException("The retained configuration export differs from its receipt.");
                        WriteAudit("reconciled", null);
                    }
                    catch (Exception exception) when (ControlledFailure(exception))
                    {
                        auditWarning = "configReconcileAuditUnconfirmed";
                    }
                }
                return Result("unchanged", null);
            }

            string id = Guid.NewGuid().ToString("N");
            receipt = new(id, context.State.LaunchId, previousHash, hash, identities.ContentIdentity,
                identities.FullIdentity, selected.ConfigReconciliation?.ReconciliationId,
                EvidencePath(paths, context.State.LaunchId, id, "receipt.json"),
                EvidencePath(paths, context.State.LaunchId, id, "config.json"),
                (utcNow?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime());
            auditPath = Path.GetFullPath(Path.Combine(paths.ProjectRoot, receipt.AuditPath));
            string exportPath = Path.GetFullPath(Path.Combine(paths.ProjectRoot, receipt.ExportPath));
            string directory = Path.GetDirectoryName(auditPath)!;
            RequirePlain(directory);
            Directory.CreateDirectory(directory);
            RequirePlain(directory);
            WriteNewFile(exportPath, bytes);
            WriteAudit("pending", null);

            RequirePlainReview(context.Staging);
            var fresh = reader.ReadContext();
            if (!fresh.Succeeded || !SameBinding(context, fresh.Context!)
                || ModBuildIdentity.ComputeConfigurationIdentities(selected.StagingPath) != identities
                || ModBuildIdentity.ComputeFile(configPath) != hash)
            {
                WriteAudit("rejected", "configReconcileBindingChanged");
                return Result("rejected", "configReconcileBindingChanged");
            }
            var updated = context.Staging with
            {
                Artifacts = context.Staging.Artifacts.Select(a => a == selected ? a with { ConfigReconciliation = receipt } : a).ToArray(),
            };
            commitAttempted = true;
            try
            {
                if (writeOwnership is null) ProjectModStager.WriteReviewOwnership(updated.OwnershipPath, updated, replace: true);
                else writeOwnership(updated.OwnershipPath, updated);
            }
            catch (Exception exception) when (ControlledFailure(exception))
            {
                // An atomic writer can finish before reporting an error. The fresh
                // owned receipt below decides whether acceptance actually happened.
            }
            var strictReader = new ProjectReviewMcpRuntimeReader(labRoot, processHost, utcNow) { HeldOperationLock = operationLock };
            var after = strictReader.ReadContext();
            if (!after.Succeeded || !SameBinding(context with { Staging = updated }, after.Context!)
                || after.Context!.Staging.Artifacts.Single(a => a.Manifest.UniqueId == selected.Manifest.UniqueId).ConfigReconciliation != receipt)
            {
                WriteAudit("unconfirmed", "configReconcileCommitUnconfirmed");
                return Result("rejected", "configReconcileCommitUnconfirmed");
            }
            try { WriteAudit("reconciled", null); }
            catch (Exception exception) when (ControlledFailure(exception))
            {
                auditWarning = "configReconcileAuditUnconfirmed";
            }
            return Result("reconciled", null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            string error = commitAttempted ? "configReconcileCommitUnconfirmed" : "configReconcileFileInvalid";
            try { if (auditPath is not null) WriteAudit(commitAttempted ? "unconfirmed" : "rejected", error); }
            catch (Exception auditError) when (auditError is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or System.Security.SecurityException)
            { }
            return Result("rejected", error);
        }

        void WriteAudit(string state, string? error)
        {
            if (auditPath is null || receipt is null || context is null || selected is null) return;
            beforeAuditWrite?.Invoke(state);
            RequirePlain(auditPath);
            if (File.Exists(auditPath))
            {
                using var previousAudit = OwnedReviewLogReader.OpenSingleLinkSnapshot(auditPath);
            }
            string temporary = auditPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteNewFile(temporary, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    State = state,
                    ErrorCode = error,
                    UniqueId = selected.Manifest.UniqueId,
                    LaunchBuildIdentity = selected.BuildIdentity,
                    Process = context.State.OwnedProcessIdentity,
                    Reconciliation = receipt,
                }, JsonOptions));
                File.Move(temporary, auditPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static bool SameBinding(ProjectReviewMcpVerifiedContext before, ProjectReviewMcpVerifiedContext after) =>
        before.AllTargetsReady && after.AllTargetsReady
        && before.State == after.State && before.Role == after.Role && before.TestSave == after.TestSave
        && JsonSerializer.Serialize(before.Staging, JsonOptions) == JsonSerializer.Serialize(after.Staging, JsonOptions);

    private static string EvidencePath(LiveLabPaths paths, string launchId, string reconciliationId, string file) =>
        Path.GetRelativePath(paths.ProjectRoot, Path.Combine(paths.SingleRoot, "review-config", launchId, reconciliationId, file)).Replace('\\', '/');

    private static void RequirePlain(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("Configuration reconciliation requires plain owned paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool ControlledFailure(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException;

    private static void RequirePlainReview(ProjectReviewStaging staging)
    {
        foreach (var artifact in staging.Artifacts)
        {
            RequirePlain(artifact.StagingPath);
            LiveLabPaths.RejectReparsePointsBelow(artifact.StagingPath);
            foreach (string file in Directory.EnumerateFiles(artifact.StagingPath, "*", SearchOption.AllDirectories))
            {
                using var snapshot = OwnedReviewLogReader.OpenSingleLinkSnapshot(file);
            }
        }
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

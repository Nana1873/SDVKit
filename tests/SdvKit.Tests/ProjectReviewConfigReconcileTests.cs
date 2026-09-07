using System.Security.Cryptography;
using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    private const string OriginalConfig = """{"GreenhouseType":"Spacious"}""";
    private const string SavedConfig = """{"GreenhouseType":"Modest"}""";

    private static PreparedReview ConfigReview(TemporaryDirectory temporary, bool targetPack = false, bool configAtLaunch = true)
    {
        var artifacts = new[]
        {
            ProjectReviewStagerTests.Artifact(temporary.Path, "Target", ProjectReviewArtifactRole.Target,
                "Test.Target", contentPackFor: targetPack ? ProjectReviewCpDiagnosis.ProviderId : null,
                kind: targetPack ? ProjectInspectionReport.ContentPack : ProjectInspectionReport.SmapiMod),
            ProjectReviewStagerTests.Artifact(temporary.Path, "ContentPatcher", ProjectReviewArtifactRole.Companion,
                ProjectReviewCpDiagnosis.ProviderId, version: "2.9.1"),
            ProjectReviewStagerTests.Artifact(temporary.Path, "Companion", ProjectReviewArtifactRole.Companion, "Test.Companion"),
            ProjectReviewStagerTests.Artifact(temporary.Path, "Pack", ProjectReviewArtifactRole.ContentPack,
                "Test.Pack", contentPackFor: ProjectReviewCpDiagnosis.ProviderId, kind: ProjectInspectionReport.ContentPack),
        };
        if (configAtLaunch)
            foreach (var artifact in artifacts)
                File.WriteAllText(Path.Combine(artifact.SourceRoot, "config.json"), OriginalConfig);
        artifacts = artifacts.Select(a => a with { BuildIdentity = ModBuildIdentity.ComputeFileSet(a.SourceRoot) }).ToArray();
        var review = PrepareSingle(temporary, artifacts, ReadyLoadedMods(artifacts
            .Select(a => new LoadedModEntry(a.Manifest.UniqueId, a.Manifest.Version, a.Manifest.Kind == ProjectInspectionReport.ContentPack))
            .Append(new LoadedModEntry("SDVKit.AlwaysOn", "0.8.0", false)).ToArray()));
        WriteLog(review.Reader, "");
        return review;
    }

    private static ConfigReconcileResult ReconcileConfig(TemporaryDirectory temporary, PreparedReview review,
        string modId = "Test.Target", ILabProcessHost? processHost = null,
        Action<string, ProjectReviewStaging>? writeOwnership = null) =>
        ProjectReviewConfigReconcile.Execute(temporary.Path, modId, processHost ?? review.ProcessHost,
            () => ObservedAt.AddSeconds(1), writeOwnership);

    private static string ConfigHash(string content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    [Theory]
    [InlineData(false, "Test.Target")]
    [InlineData(true, "Test.Target")]
    [InlineData(false, "Test.Companion")]
    [InlineData(false, "Test.Pack")]
    public void ConfigReconcileAcceptsOnlyTheSelectedSaveAndPreservesTheRunningReview(bool targetPack, string modId)
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary, targetPack);
        var paths = LiveLabPaths.Resolve(temporary.Path);
        var stateBefore = new JsonLiveLabStateStore(paths.StatePath).Read();
        var sourceBefore = review.Staging.Artifacts.ToDictionary(a => a.Manifest.UniqueId, a => ModBuildIdentity.ComputeFileSet(a.SourceRoot));
        var stagedBefore = review.Staging.Artifacts.ToDictionary(a => a.Manifest.UniqueId, a => ModBuildIdentity.ComputeFileSet(a.StagingPath));
        var selected = review.Staging.Artifacts.Single(a => a.Manifest.UniqueId == modId);
        File.WriteAllText(Path.Combine(selected.StagingPath, "config.json"), SavedConfig);
        Assert.False(review.Reader.ReadContext().Succeeded);
        int ownershipWrites = 0;
        void WriteOwnership(string path, ProjectReviewStaging staging)
        {
            Assert.Null(LiveLabOperationLock.TryAcquire(temporary.Path));
            Assert.Null(ProjectReviewActionLock.TryAcquire(paths.RuntimePath));
            ownershipWrites++;
            ProjectModStager.WriteReviewOwnership(path, staging, replace: true);
        }

        var result = ReconcileConfig(temporary, review, modId.ToLowerInvariant(), writeOwnership: WriteOwnership);

        Assert.True(result.State == "reconciled", JsonSerializer.Serialize(result));
        Assert.Null(result.ErrorCode);
        Assert.Null(result.AuditWarning);
        Assert.Equal(1, ownershipWrites);
        Assert.Equal(LaunchId, result.LaunchId);
        Assert.Equal(stateBefore!.OwnedProcessIdentity, result.Process);
        Assert.Equal(stateBefore, new JsonLiveLabStateStore(paths.StatePath).Read());
        var receipt = Assert.IsType<ConfigReconcileReceipt>(result.Reconciliation);
        Assert.Equal(ConfigHash(OriginalConfig), receipt.PreviousConfigHash);
        Assert.Equal(ConfigHash(SavedConfig), receipt.ConfigHash);
        Assert.Equal(selected.ConfigBaseline!.ContentIdentity, receipt.ContentIdentity);
        Assert.Null(receipt.PreviousReconciliationId);
        Assert.Equal(LaunchId, receipt.LaunchId);
        Assert.Equal(ModBuildIdentity.ComputeFileSet(selected.StagingPath), receipt.StagedBuildIdentity);
        Assert.NotEqual(selected.BuildIdentity, receipt.StagedBuildIdentity);
        Assert.Equal(SavedConfig, File.ReadAllText(Path.Combine(temporary.Path, receipt.ExportPath)));
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary.Path, receipt.AuditPath)));
        Assert.Equal("reconciled", audit.RootElement.GetProperty("state").GetString());
        Assert.Contains(receipt.PreviousConfigHash, audit.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(receipt.ConfigHash, audit.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.StartsWith(".sdvkit", receipt.ExportPath, StringComparison.Ordinal);
        Assert.StartsWith(".sdvkit", receipt.AuditPath, StringComparison.Ordinal);

        var accepted = review.Reader.ReadContext();
        Assert.True(accepted.Succeeded, accepted.ErrorCode);
        Assert.Equal(review.Staging.TargetLaunchState, accepted.Context!.Staging.TargetLaunchState);
        Assert.Equal(receipt, accepted.Context.Staging.Artifacts.Single(a => a.Manifest.UniqueId == modId).ConfigReconciliation);
        foreach (var artifact in review.Staging.Artifacts)
        {
            Assert.Equal(sourceBefore[artifact.Manifest.UniqueId], ModBuildIdentity.ComputeFileSet(artifact.SourceRoot));
            if (artifact.Manifest.UniqueId != modId)
                Assert.Equal(stagedBefore[artifact.Manifest.UniqueId], ModBuildIdentity.ComputeFileSet(artifact.StagingPath));
        }
    }

    [Fact]
    public void ConfigReconcileChainsDistinctSavesAndDoesNotRewriteAnUnchangedAcceptance()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        string config = Path.Combine(review.Staging.Target.StagingPath, "config.json");
        Assert.Equal("unchanged", ReconcileConfig(temporary, review).State);
        File.WriteAllText(config, SavedConfig);
        var first = ReconcileConfig(temporary, review);
        Assert.Equal("reconciled", first.State);
        string marker = File.ReadAllText(review.Staging.OwnershipPath);
        Assert.Equal("unchanged", ReconcileConfig(temporary, review,
            writeOwnership: (_, _) => throw new InvalidOperationException("Unchanged config must not rewrite ownership.")).State);
        Assert.Equal(marker, File.ReadAllText(review.Staging.OwnershipPath));
        const string nextConfig = """{"GreenhouseType":"Grand"}""";
        File.WriteAllText(config, nextConfig);

        var second = ReconcileConfig(temporary, review);

        Assert.Equal("reconciled", second.State);
        Assert.Equal(first.Reconciliation!.ReconciliationId, second.Reconciliation!.PreviousReconciliationId);
        Assert.Equal(first.Reconciliation.ConfigHash, second.Reconciliation.PreviousConfigHash);
        Assert.Equal(ConfigHash(nextConfig), second.Reconciliation.ConfigHash);
        Assert.Equal(first.Reconciliation.ContentIdentity, second.Reconciliation.ContentIdentity);
        Assert.NotEqual(first.Reconciliation.AuditPath, second.Reconciliation.AuditPath);
        Assert.Equal(SavedConfig, File.ReadAllText(Path.Combine(temporary.Path, first.Reconciliation.ExportPath)));
        Assert.Equal(nextConfig, File.ReadAllText(Path.Combine(temporary.Path, second.Reconciliation.ExportPath)));
        Assert.True(review.Reader.ReadContext().Succeeded);
    }

    [Fact]
    public async Task ConfigReconcileLetsTheExistingMcpClientResumeRuntimeAndDiagnostics()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        await using McpTestClient harness = await McpTestClient.StartAsync(ProjectReviewMcpServer.CreateOptions(review.Reader));
        var before = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpServer.RuntimeToolName, Args(), cancellationToken: harness.Token));
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        var drifted = await harness.Client.CallToolAsync(
            ProjectReviewMcpServer.RuntimeToolName, Args(), cancellationToken: harness.Token);
        Assert.True(drifted.IsError);

        Assert.Equal("reconciled", ReconcileConfig(temporary, review).State);

        var after = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpServer.RuntimeToolName, Args(), cancellationToken: harness.Token));
        var diagnostics = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpLogTools.ToolName, Args(("modId", "Test.Target")), cancellationToken: harness.Token));
        Assert.Equal(before.GetProperty("launchId").GetString(), after.GetProperty("launchId").GetString());
        Assert.Equal(before.GetProperty("target").GetProperty("buildIdentity").GetString(),
            after.GetProperty("target").GetProperty("buildIdentity").GetString());
        Assert.Equal("ready", diagnostics.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("deletedConfig")]
    [InlineData("manifest")]
    [InlineData("assembly")]
    [InlineData("content")]
    [InlineData("extraFile")]
    [InlineData("nestedConfig")]
    [InlineData("otherConfig")]
    [InlineData("foreignMod")]
    [InlineData("foreignProfile")]
    [InlineData("foreignTopology")]
    [InlineData("foreignLaunch")]
    [InlineData("foreignProcess")]
    [InlineData("staleStatus")]
    [InlineData("missingOwnership")]
    public void ConfigReconcileRejectsUnrelatedDriftOrForeignOwnershipWithoutChangingFiles(string failure)
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        var paths = LiveLabPaths.Resolve(temporary.Path);
        string config = Path.Combine(review.Staging.Target.StagingPath, "config.json");
        File.WriteAllText(config, SavedConfig);
        var state = new JsonLiveLabStateStore(paths.StatePath).Read()!;
        switch (failure)
        {
            case "malformed": File.WriteAllText(config, "{ invalid"); break;
            case "deletedConfig": File.Delete(config); break;
            case "manifest": File.AppendAllText(Path.Combine(review.Staging.Target.StagingPath, "manifest.json"), " "); break;
            case "assembly": File.AppendAllText(Path.Combine(review.Staging.Target.StagingPath, review.Staging.Target.Manifest.EntryDll!), "changed"); break;
            case "content": File.AppendAllText(Path.Combine(review.Staging.Artifacts[^1].StagingPath, "interiors.json"), "changed"); break;
            case "extraFile": File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "injected.json"), "{}"); break;
            case "nestedConfig":
                Directory.CreateDirectory(Path.Combine(review.Staging.Target.StagingPath, "nested"));
                File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "nested", "config.json"), "{}");
                break;
            case "otherConfig": File.WriteAllText(Path.Combine(review.Staging.Artifacts[^1].StagingPath, "config.json"), SavedConfig); break;
            case "foreignProfile": state = state with { ModsPath = Path.Combine(temporary.Path, "foreign", "Mods") }; break;
            case "foreignTopology": state = state with { Topology = NetworkTwoContract.Topology }; break;
            case "foreignLaunch": state = state with { LaunchId = Guid.NewGuid().ToString("N") }; break;
            case "foreignProcess": state = state with { OwnedProcessIdentity = state.OwnedProcessIdentity with { ProcessId = ProcessId + 1 } }; break;
            case "staleStatus":
                var status = JsonSerializer.Deserialize<AlwaysOnStatusMarker>(File.ReadAllText(paths.StatusPath), LiveLabJsonOptions.CamelCase)!;
                File.WriteAllText(paths.StatusPath, JsonSerializer.Serialize(status with { ObservedAtUtc = StartedAt.AddHours(-1) }, LiveLabJsonOptions.CamelCase));
                break;
            case "missingOwnership": File.Delete(review.Staging.OwnershipPath); break;
        }
        File.WriteAllText(paths.StatePath, JsonSerializer.Serialize(state, LiveLabJsonOptions.CamelCase));
        string stateBefore = File.ReadAllText(paths.StatePath);
        string? markerBefore = File.Exists(review.Staging.OwnershipPath) ? File.ReadAllText(review.Staging.OwnershipPath) : null;
        var stagedBefore = review.Staging.Artifacts.Select(a => ModBuildIdentity.ComputeFileSet(a.StagingPath)).ToArray();
        var sourceBefore = review.Staging.Artifacts.Select(a => ModBuildIdentity.ComputeFileSet(a.SourceRoot)).ToArray();

        var result = ReconcileConfig(temporary, review, failure == "foreignMod" ? "Foreign.Pack" : "Test.Target",
            writeOwnership: (_, _) => throw new InvalidOperationException("Rejected input must not write ownership."));

        Assert.Equal("rejected", result.State);
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(markerBefore, File.Exists(review.Staging.OwnershipPath) ? File.ReadAllText(review.Staging.OwnershipPath) : null);
        Assert.Equal(stateBefore, File.ReadAllText(paths.StatePath));
        Assert.Equal(stagedBefore, review.Staging.Artifacts.Select(a => ModBuildIdentity.ComputeFileSet(a.StagingPath)));
        Assert.Equal(sourceBefore, review.Staging.Artifacts.Select(a => ModBuildIdentity.ComputeFileSet(a.SourceRoot)));
    }

    [Fact]
    public void ConfigReconcileDoesNotEnrollANewConfigIntoTheLaunchBaseline()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary, configAtLaunch: false);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);

        var result = ReconcileConfig(temporary, review);

        Assert.Equal("rejected", result.State);
        Assert.Equal("configReconcileBaselineMissing", result.ErrorCode);
        Assert.Null(ProjectModStager.ReadReview(LiveLabPaths.Resolve(temporary.Path)).Staging!.Target.ConfigReconciliation);
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("{\"GreenhouseType\":\"Modest\",}", true)]
    [InlineData("{/*Saved by GMCM*/\"GreenhouseType\":\"Modest\"}", true)]
    public void ConfigReconcileRequiresAnObjectButAcceptsSmapiCompatibleJson(string json, bool accepted)
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), json);

        var result = ReconcileConfig(temporary, review);

        Assert.Equal(accepted ? "reconciled" : "rejected", result.State);
        Assert.Equal(accepted ? null : "configReconcileInvalidJson", result.ErrorCode);
        if (accepted) Assert.Equal(json, File.ReadAllText(Path.Combine(temporary.Path, result.Reconciliation!.ExportPath)));
    }

    [Fact]
    public void ConfigReconcileRejectsAnOversizedConfigBeforeExport()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"),
            "{\"value\":\"" + new string('x', ProjectReviewConfigReconcile.MaximumConfigBytes) + "\"}");

        var result = ReconcileConfig(temporary, review);

        Assert.Equal("configReconcileConfigTooLarge", result.ErrorCode);
        Assert.Null(result.Reconciliation);
    }

    [Theory]
    [InlineData("hardLink")]
    [InlineData("junction")]
    public void ConfigReconcileRejectsLinkedConfigOrStagingWithoutChangingTheForeignFile(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        string config = Path.Combine(review.Staging.Target.StagingPath, "config.json");
        string foreignRoot = Path.Combine(temporary.Path, "foreign");
        string foreignConfig = Path.Combine(foreignRoot, "config.json");
        if (kind == "junction")
        {
            Directory.Move(review.Staging.Target.StagingPath, foreignRoot);
            File.WriteAllText(foreignConfig, SavedConfig);
            new Win32DirectChildJunctionPlatform().CreateDirectoryJunction(review.Staging.Target.StagingPath, foreignRoot);
        }
        else
        {
            Directory.CreateDirectory(foreignRoot);
            File.WriteAllText(foreignConfig, SavedConfig);
            File.Delete(config);
            Assert.True(CreateHardLink(config, foreignConfig, IntPtr.Zero));
        }
        string marker = File.ReadAllText(review.Staging.OwnershipPath);
        try
        {
            var result = ReconcileConfig(temporary, review);

            Assert.Equal("rejected", result.State);
            Assert.NotNull(result.ErrorCode);
            Assert.Equal(marker, File.ReadAllText(review.Staging.OwnershipPath));
            Assert.Equal(SavedConfig, File.ReadAllText(foreignConfig));
            Assert.Equal(OriginalConfig, File.ReadAllText(Path.Combine(review.Staging.Target.SourceRoot, "config.json")));
        }
        finally
        {
            if (kind == "junction")
                new Win32DirectChildJunctionPlatform().DeleteExactDirectoryJunction(review.Staging.Target.StagingPath, foreignRoot);
        }
    }

    [Theory]
    [InlineData("Exited")]
    [InlineData("IdentityMismatch")]
    [InlineData("Unreadable")]
    public void ConfigReconcileRequiresTheExactProcessToBeRunning(string status)
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        string marker = File.ReadAllText(review.Staging.OwnershipPath);

        var result = ReconcileConfig(temporary, review, processHost: new ConfigProcessHost(Enum.Parse<LabProcessInspectStatus>(status)));

        Assert.Equal("rejected", result.State);
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(marker, File.ReadAllText(review.Staging.OwnershipPath));
    }

    [Fact]
    public void ConfigReconcileUsesTheExistingOperationAndActionLocks()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        using (var held = LiveLabOperationLock.TryAcquire(temporary.Path))
            Assert.Equal("reviewBusy", ReconcileConfig(temporary, review).ErrorCode);
        using (var held = ProjectReviewActionLock.TryAcquire(LiveLabPaths.Resolve(temporary.Path).RuntimePath))
            Assert.Equal("reviewBusy", ReconcileConfig(temporary, review).ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigReconcileChecksWhetherAFailedOwnershipWriteActuallyCommittedAndRetainsCleanup(bool writeCompleted)
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        var paths = LiveLabPaths.Resolve(temporary.Path);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        string marker = File.ReadAllText(review.Staging.OwnershipPath);

        var result = ReconcileConfig(temporary, review, writeOwnership: (path, staging) =>
        {
            if (writeCompleted) ProjectModStager.WriteReviewOwnership(path, staging, replace: true);
            throw new IOException("Injected marker-write failure.");
        });

        Assert.Equal(writeCompleted ? "reconciled" : "rejected", result.State);
        Assert.Equal(writeCompleted ? null : "configReconcileCommitUnconfirmed", result.ErrorCode);
        if (!writeCompleted) Assert.NotEqual("none", result.Recovery);
        if (!writeCompleted) Assert.Equal(marker, File.ReadAllText(review.Staging.OwnershipPath));
        Assert.Equal(writeCompleted, review.Reader.ReadContext().Succeeded);
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary.Path, result.Reconciliation!.AuditPath)));
        Assert.Equal(writeCompleted ? "reconciled" : "unconfirmed", audit.RootElement.GetProperty("state").GetString());
        Assert.Null(ProjectModStager.ReadReviewForCleanup(paths, "single").Problem);
        Assert.True(ProjectModStager.RemoveReview(paths).Removed);
        Assert.False(Directory.Exists(review.Staging.Target.StagingPath));
        Assert.Equal(OriginalConfig, File.ReadAllText(Path.Combine(review.Staging.Target.SourceRoot, "config.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigReconcileAuditFailureKeepsConfirmedAcceptanceAndRepairsOnlyVerifiedExports(bool exportChanged)
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);

        var result = ProjectReviewConfigReconcile.Execute(temporary.Path, "Test.Target", review.ProcessHost,
            () => ObservedAt.AddSeconds(1), beforeAuditWrite: state =>
            {
                if (state == "reconciled") throw new IOException("Injected final audit failure.");
            });

        Assert.Equal("reconciled", result.State);
        Assert.Null(result.ErrorCode);
        Assert.Equal("configReconcileAuditUnconfirmed", result.AuditWarning);
        Assert.NotEqual("none", result.Recovery);
        Assert.True(review.Reader.ReadContext().Succeeded);
        string auditPath = Path.Combine(temporary.Path, result.Reconciliation!.AuditPath);
        using (var audit = JsonDocument.Parse(File.ReadAllText(auditPath)))
            Assert.Equal("pending", audit.RootElement.GetProperty("state").GetString());
        string marker = File.ReadAllText(review.Staging.OwnershipPath);
        string exportPath = Path.Combine(temporary.Path, result.Reconciliation.ExportPath);
        if (exportChanged) File.WriteAllText(exportPath, "{}");

        var retry = ReconcileConfig(temporary, review);

        Assert.Equal("unchanged", retry.State);
        Assert.Equal(result.Reconciliation, retry.Reconciliation);
        Assert.Equal(exportChanged ? "configReconcileAuditUnconfirmed" : null, retry.AuditWarning);
        Assert.Equal(marker, File.ReadAllText(review.Staging.OwnershipPath));
        Assert.Equal(exportChanged ? "{}" : SavedConfig, File.ReadAllText(exportPath));
        using (var audit = JsonDocument.Parse(File.ReadAllText(auditPath)))
            Assert.Equal(exportChanged ? "pending" : "reconciled", audit.RootElement.GetProperty("state").GetString());
        Assert.Null(ProjectModStager.ReadReviewForCleanup(LiveLabPaths.Resolve(temporary.Path), "single").Problem);
    }

    [Fact]
    public void ConfigReconcileRejectsAProcessExitDuringExportBeforeCommittingOwnership()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        string marker = File.ReadAllText(review.Staging.OwnershipPath);

        var result = ReconcileConfig(temporary, review,
            processHost: new ConfigProcessHost(LabProcessInspectStatus.Exited, successfulInspections: 1),
            writeOwnership: (_, _) => throw new InvalidOperationException("Changed process must not commit."));

        Assert.Equal("rejected", result.State);
        Assert.Equal("configReconcileBindingChanged", result.ErrorCode);
        Assert.Equal(marker, File.ReadAllText(review.Staging.OwnershipPath));
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary.Path, result.Reconciliation!.AuditPath)));
        Assert.Equal("rejected", audit.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void ConfigReconcileSuccessfulCleanupRemovesOwnedStagingButRetainsTheAuditAndSource()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary);
        var paths = LiveLabPaths.Resolve(temporary.Path);
        string protectedFile = temporary.WriteFile("normal-mod-manager-Mods/sentinel.txt", "untouched");
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        var result = ReconcileConfig(temporary, review);
        Assert.Equal("reconciled", result.State);

        Assert.Null(ProjectModStager.ReadReviewForCleanup(paths, "single").Problem);
        Assert.True(ProjectModStager.RemoveReview(paths).Removed);

        Assert.All(review.Staging.Artifacts, artifact => Assert.False(Directory.Exists(artifact.StagingPath)));
        Assert.False(File.Exists(review.Staging.OwnershipPath));
        Assert.Equal(SavedConfig, File.ReadAllText(Path.Combine(temporary.Path, result.Reconciliation!.ExportPath)));
        Assert.True(File.Exists(Path.Combine(temporary.Path, result.Reconciliation.AuditPath)));
        Assert.Equal(OriginalConfig, File.ReadAllText(Path.Combine(review.Staging.Target.SourceRoot, "config.json")));
        Assert.Equal("untouched", File.ReadAllText(protectedFile));
    }

    [Fact]
    public void ConfigReconcileAndCpRefreshRemainSeparateReviewOperations()
    {
        using TemporaryDirectory temporary = new();
        var review = ConfigReview(temporary, targetPack: true);
        File.WriteAllText(Path.Combine(review.Staging.Target.StagingPath, "config.json"), SavedConfig);
        Assert.Equal("reconciled", ReconcileConfig(temporary, review).State);

        var result = ProjectReviewCpRefresh.Execute(temporary.Path, review.Staging.Target.SourceRoot, "Test.Target",
            ProjectReviewCpDiagnosis.ProviderId, ["interiors.json"], "Data/Objects", "388", review.ProcessHost,
            () => ObservedAt.AddSeconds(1), _ => throw new InvalidOperationException("Mixed refresh must not dispatch."));

        Assert.Equal("rejected", result.State);
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(0, result.FilesReplaced);
        Assert.True(review.Reader.ReadContext().Succeeded);
    }

    private sealed class ConfigProcessHost(LabProcessInspectStatus status, int successfulInspections = 0) : ILabProcessHost
    {
        private int _inspections;
        public LabProcessInspectResult Inspect(OwnedProcessIdentity expected) =>
            new(_inspections++ < successfulInspections ? LabProcessInspectStatus.Running : status);
        public LabProcessStartResult Start(LabProcessStartSpec specification) => throw new InvalidOperationException("Reconcile must not start a process.");
        public LabProcessWaitResult WaitForExit(OwnedProcessIdentity expected, TimeSpan timeout) => throw new InvalidOperationException("Reconcile must not wait for exit.");
        public LabProcessCloseResult RequestCloseAndWait(OwnedProcessIdentity expected, TimeSpan timeout) => throw new InvalidOperationException("Reconcile must not close a process.");
    }
}

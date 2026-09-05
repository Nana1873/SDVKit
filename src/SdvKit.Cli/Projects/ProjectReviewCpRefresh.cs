using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record CpRefreshResult(string State, string? ErrorCode, string Recovery,
    string? LaunchId, OwnedProcessIdentity? Process, string? LaunchBuildIdentity,
    CpRefreshReceipt? Refresh, int FilesReplaced, bool StagingRestored,
    CpDiagnosisResult? Diagnosis, object? Observation, double ElapsedSeconds);

internal static class ProjectReviewCpRefresh
{
    internal const int MaximumFiles = 16;
    internal const int MaximumFileBytes = 1024 * 1024;
    internal const int MaximumSelectionBytes = 4 * 1024 * 1024;
    private const string Recovery = "Do not retry this refresh. Run project review status and cp-diagnose to inspect available evidence, then project review stop, reset, and start the exact selection again.";
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static bool ValidFiles(IReadOnlyList<string>? files) => files is { Count: > 0 and <= MaximumFiles }
        && files.All(ValidRelativeJson)
        && files.Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Count;

    private static bool ValidRelativeJson(string file) => !string.IsNullOrWhiteSpace(file) && file.Length <= 240
        && file.EndsWith(".json", StringComparison.Ordinal) && !Path.IsPathRooted(file)
        && file.Split('/').All(part => part.Length > 0 && part is not ("." or "..")
            && part == part.Trim() && !part.EndsWith('.') && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' '))
        && file is not ("manifest.json" or "config.json") && !file.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase);

    internal static CpRefreshResult Execute(string labRoot, string sourceRoot, string packId,
        string providerId, IReadOnlyList<string> files, string asset, string key,
        ILabProcessHost? processHost = null, Func<DateTimeOffset>? utcNow = null,
        Func<string, LiveLabCommandResult>? send = null, Action<string, string>? replace = null,
        TimeSpan? responseTimeout = null)
    {
        var timer = Stopwatch.StartNew();
        ProjectReviewMcpVerifiedContext? context = null;
        CpRefreshReceipt? receipt = null;
        CpDiagnosisResult? diagnosis = null;
        object? observation = null;
        int replaced = 0;
        bool restored = false, mutationStarted = false;
        CpRefreshResult Result(string state, string? error) => new(state, error,
            mutationStarted && (receipt?.RequiresRestart != false) ? Recovery : "none",
            context?.State.LaunchId, context?.State.OwnedProcessIdentity, context?.Staging.Target.BuildIdentity,
            receipt, replaced, restored, diagnosis, observation, timer.Elapsed.TotalSeconds);
        if (!ValidFiles(files) || !ProjectReviewCpDiagnosis.ValidArguments(packId, providerId, asset, null)
            || string.IsNullOrWhiteSpace(key) || key.Length > ReviewDataContract.MaximumKeyLength || key.Any(char.IsControl))
            return Result("rejected", "cpRefreshArgumentsInvalid");
        try
        {
            LiveLabPaths paths = LiveLabPaths.Resolve(labRoot);
            using var operationLock = LiveLabOperationLock.TryAcquire(labRoot);
            if (operationLock is null) return Result("rejected", "reviewBusy");
            using var actionLock = ProjectReviewActionLock.TryAcquire(paths.RuntimePath);
            if (actionLock is null) return Result("rejected", "reviewBusy");
            var reader = new ProjectReviewMcpRuntimeReader(labRoot, processHost, utcNow) { HeldOperationLock = operationLock };
            var verified = reader.ReadContext();
            if (!verified.Succeeded) return Result("rejected", verified.ErrorCode);
            context = verified.Context!;
            ProjectReviewOwnedArtifact target = context.Staging.Target;
            if (!target.Manifest.UniqueId.Equals(packId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(target.Manifest.ContentPackFor, ProjectReviewCpDiagnosis.ProviderId, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(target.SourceRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return Result("rejected", "cpRefreshSelectionMismatch");
            var provider = context.Staging.Artifacts.SingleOrDefault(a => a.Manifest.UniqueId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (provider?.Manifest.Version != "2.9.1") return Result("rejected", "cpVersionUnsupported");
            packId = target.Manifest.UniqueId;
            providerId = provider.Manifest.UniqueId;
            if (!context.AllTargetsReady || context.AlwaysOn.LoadedMods?.State != "ready"
                || !context.AlwaysOn.LoadedMods.Mods.Any(m => m.UniqueId.Equals(packId, StringComparison.OrdinalIgnoreCase) && m.IsContentPack
                    && m.Version == ProjectModLaunchState.NormalizeVersion(target.Manifest.Version))
                || !context.AlwaysOn.LoadedMods.Mods.Any(m => m.UniqueId.Equals(providerId, StringComparison.OrdinalIgnoreCase) && !m.IsContentPack && m.Version == "2.9.1"))
                return Result("rejected", "cpSelectedModsNotLoaded");
            if (target.CpRefresh?.RequiresRestart == true)
            {
                receipt = target.CpRefresh;
                mutationStarted = true;
                return Result("rejected", "cpRefreshRestartRequired");
            }

            foreach (var artifact in context.Staging.Artifacts)
            {
                RequireBoundedPlainTree(artifact.StagingPath);
                RequireBoundedPlainTree(artifact.SourceRoot);
                if (artifact != target && (artifact.BuildLog is not null
                    || ModBuildIdentity.ComputeFileSet(artifact.SourceRoot) != ModBuildIdentity.ComputeFileSet(artifact.StagingPath)))
                    throw new InvalidDataException("cpRefreshCompanionChangedRestartRequired");
            }

            string preparationParent = Path.Combine(paths.SingleRoot, "review-prepared");
            if (Directory.Exists(preparationParent) && ProjectChecker.HasLinkedAncestor(preparationParent))
                throw new InvalidDataException("cpRefreshLinkedPath");
            string preparation = Path.Combine(preparationParent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(preparation);
            if (ProjectChecker.HasLinkedAncestor(preparation)) throw new InvalidDataException("cpRefreshLinkedPath");
            var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
            var originals = new Dictionary<string, string>(StringComparer.Ordinal);
            long selectedBytes = 0;
            foreach (string file in files)
            {
                byte[] bytes = ReadPatch(Path.Combine(target.SourceRoot, file));
                byte[] original = ReadPatch(Path.Combine(target.StagingPath, file));
                selectedBytes += bytes.Length + original.Length;
                if (selectedBytes > MaximumSelectionBytes) throw new InvalidDataException("cpRefreshSelectionTooLarge");
                string candidate = Path.Combine(preparation, "candidate", file);
                string backup = Path.Combine(preparation, "original", file);
                Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.WriteAllBytes(candidate, bytes);
                File.WriteAllBytes(backup, original);
                candidates.Add(file, candidate);
                originals.Add(file, backup);
                bool include = file != "content.json";
                var problems = ProjectChecker.CheckPatchFile(Path.Combine(preparation, "candidate"), file, include);
                if (problems.Count > 0) throw new InvalidDataException("cpRefreshInvalidPatch: " + file + ": " + problems[0].Message);
                if (!include)
                {
                    var before = Parse(original);
                    var after = Parse(bytes);
                    before.Remove("Changes");
                    after.Remove("Changes");
                    if (!JsonNode.DeepEquals(before, after)) throw new InvalidDataException("cpRefreshNonPatchChangeRestartRequired");
                }
            }
            ValidateIncludes(target.StagingPath, candidates, files);
            string nextIdentity = ModBuildIdentity.ComputeFileSetWithReplacements(target.StagingPath, candidates);
            if (nextIdentity != ModBuildIdentity.ComputeFileSet(target.SourceRoot))
                throw new InvalidDataException("cpRefreshUnselectedSourceChangeRestartRequired");
            if (nextIdentity == target.StagedBuildIdentity) return Result("rejected", "cpRefreshNoChanges");

            // Revalidate immediately before committing authorization. The existing
            // operation lock covers copies, console delivery, diagnosis and observation.
            var fresh = reader.ReadContext();
            if (!fresh.Succeeded || fresh.Context!.State != context.State
                || !OwnedReviewLogReader.SameStagedContent(context.Staging, fresh.Context.Staging))
                return Result("rejected", "cpRefreshBindingChanged");
            receipt = new(Guid.NewGuid().ToString("N"), context.State.LaunchId, target.StagedBuildIdentity, nextIdentity, files.ToArray(), null, true);
            ProjectReviewStaging WithReceipt(CpRefreshReceipt value) => context.Staging with
            {
                Artifacts = context.Staging.Artifacts.Select(a => a == target ? a with { CpRefresh = value } : a).ToArray(),
            };
            void Persist() => ProjectModStager.WriteReviewOwnership(context.Staging.OwnershipPath, WithReceipt(receipt!), replace: true);
            mutationStarted = true;
            Persist();
            replace ??= ReplaceFile;
            try
            {
                foreach (string file in files)
                {
                    replace(candidates[file], Path.Combine(target.StagingPath, file));
                    replaced++;
                }
                if (ModBuildIdentity.ComputeFileSet(target.StagingPath) != nextIdentity)
                    throw new InvalidDataException("cpRefreshCopyIdentityMismatch");
            }
            catch (Exception e) when (Controlled(e))
            {
                // No reload was attempted. Restore every selected file, including a
                // destination whose replacement may have thrown after writing it.
                try
                {
                    foreach (string file in files) replace(originals[file], Path.Combine(target.StagingPath, file));
                    restored = ModBuildIdentity.ComputeFileSet(target.StagingPath) == target.StagedBuildIdentity;
                }
                catch (Exception rollback) when (Controlled(rollback)) { restored = false; }
                receipt = receipt with { CommandWritten = false, StagedBuildIdentity = restored ? target.StagedBuildIdentity : nextIdentity };
                Persist();
                return Result("incomplete", restored ? "cpRefreshCopyFailedRestored" : "cpRefreshRollbackIncomplete");
            }
            send ??= command => ProjectReviewService.ExecuteCommand(command, LiveLabState.SingleTopology, null, labRoot,
                heldOperationLock: operationLock);
            diagnosis = ProjectReviewCpDiagnosis.Execute(reader, packId, providerId, asset, null,
                send, responseTimeout, actionLock, reload: true);
            receipt = receipt with
            {
                CommandWritten = diagnosis.Reload?.CommandMayHaveBeenWritten == false
                ? false : diagnosis.Reload?.CommandWritten == true ? true : null
            };
            Persist();
            if (diagnosis.State != "ready") return Result("incomplete", diagnosis.ErrorCode ?? "cpRefreshDiagnosisIncomplete");
            var observed = ProjectReviewDataService.Execute(new ReviewDataQuery("get", asset, key, 0, 1),
                labRoot, responseTimeout: responseTimeout, send: send);
            observation = observed.Report;
            var afterObservation = reader.ReadContext();
            if (!afterObservation.Succeeded || afterObservation.Context!.State != context.State
                || afterObservation.Context.Staging.Target.StagedBuildIdentity != nextIdentity)
                return Result("incomplete", "cpRefreshBindingChanged");
            if (observed.ExitCode != 0) return Result("incomplete", "cpRefreshObservationIncomplete");
            receipt = receipt with { RequiresRestart = false };
            Persist();
            return Result("observed", null);
        }
        catch (Exception e) when (Controlled(e))
        {
            if (mutationStarted && receipt is not null) receipt = receipt with { RequiresRestart = true };
            return Result(mutationStarted ? "incomplete" : "rejected",
                e is InvalidDataException ? e.Message : "cpRefreshIoFailure");
        }
    }

    private static bool Controlled(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or System.Security.SecurityException or JsonException;

    private static byte[] ReadPatch(string path)
    {
        RequirePlain(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        OwnedReviewLogReader.RequireSingleLink(stream);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("cpRefreshFileTooLarge");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes.AsSpan(3) : bytes, documentOptions: JsonOptions) as JsonObject
        ?? throw new InvalidDataException("cpRefreshInvalidPatch");

    private static void RequirePlain(string path)
    {
        if (ProjectChecker.HasLinkedAncestor(Path.GetDirectoryName(path)!)
            || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("cpRefreshLinkedPath");
    }

    private static void RequireBoundedPlainTree(string root)
    {
        if (ProjectChecker.HasLinkedAncestor(root)) throw new InvalidDataException("cpRefreshLinkedPath");
        var pending = new Stack<string>();
        pending.Push(root);
        long bytes = 0;
        int entries = 0;
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 4096) throw new InvalidDataException("cpRefreshPackTooLarge");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("cpRefreshLinkedPath");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                OwnedReviewLogReader.RequireSingleLink(stream);
                bytes += stream.Length;
                if (bytes > 256L * 1024 * 1024) throw new InvalidDataException("cpRefreshPackTooLarge");
            }
        }
    }

    private static void ValidateIncludes(string stagedRoot, Dictionary<string, string> candidates, IReadOnlyList<string> selected)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string file)
        {
            if (active.Contains(file)) throw new InvalidDataException("cpRefreshIncludeCycle");
            if (!visited.Add(file)) return;
            if (visited.Count > 64) throw new InvalidDataException("cpRefreshIncludeLimit");
            active.Add(file);
            JsonObject document = Parse(ReadPatch(candidates.TryGetValue(file, out string? prepared) ? prepared : Path.Combine(stagedRoot, file)));
            if (document["Changes"] is not JsonArray changes) throw new InvalidDataException("cpRefreshInvalidPatch");
            foreach (JsonNode? patch in changes)
            {
                if (patch is not JsonObject obj) throw new InvalidDataException("cpRefreshInvalidPatch");
                if (obj["Action"]?.GetValue<string>() != "Include") continue;
                string? from = obj["FromFile"]?.GetValue<string>();
                if (from is null) throw new InvalidDataException("cpRefreshIncludeUnsupportedRestartRequired");
                foreach (string path in from.Split(',').Select(s => s.Trim()))
                {
                    if (!ValidRelativeJson(path)) throw new InvalidDataException("cpRefreshIncludeUnsupportedRestartRequired");
                    Visit(path);
                }
            }
            active.Remove(file);
        }
        Visit("content.json");
        if (selected.Any(file => !visited.Contains(file))) throw new InvalidDataException("cpRefreshFileNotIncluded");
    }

    private static void ReplaceFile(string prepared, string destination)
    {
        RequirePlain(destination);
        using (var current = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            OwnedReviewLogReader.RequireSingleLink(current);
        string temporary = destination + ".sdvkit-refresh-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = ReadPatch(prepared);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            RequirePlain(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

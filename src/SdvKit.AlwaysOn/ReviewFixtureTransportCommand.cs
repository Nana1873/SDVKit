using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.FarmAnimals;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
#endif

namespace SdvKit.AlwaysOn;

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewFixtureTransportCommand
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewFixtureRuntime runtime,
        Func<TestSaveAutomation?> testSave,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(testSave);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-fixture runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        _ = ReviewFixtureTransportArguments.TryParse(
            arguments,
            out string? requestId,
            out ReviewFixtureRequestBinding? binding,
            out ReviewFixtureQuery? query,
            out ReviewFixtureRequest? request,
            out ReviewFixtureProblem? parseProblem);
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-fixture rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        if (binding is null)
        {
            monitor.Log(
                "SDVKit review-fixture rejected an invalid identity binding.",
                LogLevel.Error);
            return;
        }

        string operation = query?.Operation
            ?? (arguments.Length > 7 ? arguments[7] : "unknown");
        if (parseProblem is not null)
        {
            ReviewFixtureAccess parseAccess = runtime.VerifyExactReviewFixture();
            Publish(
                runtimePath,
                requestId!,
                binding,
                operation,
                parseAccess,
                new ReviewFixtureResult(false, parseProblem.Message),
                [parseProblem],
                save: null,
                monitor);
            return;
        }

        if (operation == ReviewFixtureTransportContract.SaveOperation)
        {
            ReviewFixtureExecution verification = ReviewFixtureOperation.VerifyBound(
                requiresMainPlayer: true,
                runtime,
                binding);
            ReviewFixtureAccess access = verification.Access;
            if (!verification.Result.Succeeded)
            {
                ReviewFixtureProblem problem = verification.Problem
                    ?? new ReviewFixtureProblem(
                        "fixtureIdentityRejected",
                        verification.Result.Message);
                Publish(
                    runtimePath,
                    requestId!,
                    binding,
                    operation,
                    access,
                    verification.Result,
                    [problem],
                    save: null,
                    monitor);
                return;
            }

            TestSaveAutomation? automation = testSave();
            string reason =
                "The exact disposable test-save automation is unavailable.";
            bool started = automation is not null
                && automation.TryStartReviewSave(
                    (succeeded, message) => Publish(
                        runtimePath,
                        requestId!,
                        binding,
                        operation,
                        access,
                        new ReviewFixtureResult(succeeded, message),
                        succeeded
                            ? []
                            : [new ReviewFixtureProblem("fixtureSaveFailed", message)],
                        succeeded
                            ? new ReviewFixtureSaveReport(
                                Constants.SaveFolderName!,
                                DateTimeOffset.UtcNow)
                            : null,
                        monitor),
                    out reason);
            if (!started)
            {
                string message = automation is null
                    ? "The exact disposable test-save automation is unavailable."
                    : reason;
                Publish(
                    runtimePath,
                    requestId!,
                    binding,
                    operation,
                    access,
                    new ReviewFixtureResult(false, message),
                    [new ReviewFixtureProblem("fixtureSaveRejected", message)],
                    save: null,
                    monitor);
            }

            return;
        }

        void PublishExecution(ReviewFixtureExecution execution)
        {
            ReviewFixtureResult completedResult = execution.Result;
            Publish(
                runtimePath,
                requestId!,
                binding,
                operation,
                execution.Access,
                completedResult,
                completedResult.Succeeded
                    ? []
                    : [execution.Problem
                        ?? new ReviewFixtureProblem(
                            "fixtureActionRejected",
                            completedResult.Message)],
                save: null,
                monitor);
        }

        if (request is ReviewFixtureEnterRequest or ReviewFixtureFarmRequest)
        {
            try
            {
                ReviewFixtureNavigationOperation.ExecuteBound(
                    request,
                    runtime,
                    binding,
                    PublishExecution);
            }
            catch (Exception exception)
            {
                ReviewFixtureAccess failedAccess = runtime.VerifyExactReviewFixture();
                PublishExecution(new ReviewFixtureExecution(
                    failedAccess,
                    new ReviewFixtureResult(
                        false,
                        $"The fixture action failed closed ({exception.GetType().Name}).")));
            }

            return;
        }

        ReviewFixtureExecution execution;
        try
        {
            execution = ReviewFixtureOperation.ExecuteBound(request!, runtime, binding);
        }
        catch (Exception exception)
        {
            ReviewFixtureAccess failedAccess = runtime.VerifyExactReviewFixture();
            execution = new ReviewFixtureExecution(
                failedAccess,
                new ReviewFixtureResult(
                    false,
                    $"The fixture action failed closed ({exception.GetType().Name})."));
        }

        PublishExecution(execution);
    }

    private static void Publish(
        string runtimePath,
        string requestId,
        ReviewFixtureRequestBinding binding,
        string operation,
        ReviewFixtureAccess access,
        ReviewFixtureResult result,
        IReadOnlyList<ReviewFixtureProblem> problems,
        ReviewFixtureSaveReport? save,
        IMonitor monitor)
    {
        string? networkRole = Environment
            .GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")
            ?.Trim();
        bool network = networkRole is not null
            && NetworkTwoContract.IsRole(networkRole);
        string topology = network
            ? NetworkTwoContract.Topology
            : "single";
        string? role = network ? networkRole : null;
        string launchId = access.LaunchId
            ?? Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")?.Trim()
            ?? string.Empty;
        topology = access.Topology ?? topology;
        role = string.Equals(
                access.Role,
                ReviewFixtureTransportContract.SingleRoleToken,
                StringComparison.Ordinal)
            ? null
            : access.Role ?? role;
        var report = new ReviewFixtureReport(
            ReviewFixtureTransportContract.SchemaVersion,
            result.Succeeded ? "ready" : "blocked",
            operation,
            launchId,
            topology,
            role,
            DateTimeOffset.UtcNow,
            access.FixtureId,
            access.SaveId,
            result.Message,
            problems,
            result.Status,
            result.Navigation,
            result.Building,
            result.Animal,
            save);
        var envelope = new ReviewFixtureResponseEnvelope(
            ReviewFixtureTransportContract.SchemaVersion,
            requestId,
            binding,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-fixture completed '{operation}' with state '{report.State}'.",
                result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit review-fixture could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    private static void WriteResponse(
        string runtimePath,
        ReviewFixtureResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("The review runtime path is required.", nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string responsePath = ReviewFixtureTransportContract.ResponsePath(
            Path.GetFullPath(runtimePath),
            envelope.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ResponseJsonOptions);
        if (bytes.Length > ReviewFixtureTransportContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-fixture response exceeds its maximum size.");
        }
        ReviewResponseFile.Write(responsePath, bytes);
    }
}
#endif

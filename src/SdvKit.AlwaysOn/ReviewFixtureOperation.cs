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

internal sealed record ReviewFixtureAccess(
    bool Succeeded,
    bool CanMutate,
    string? FixtureId,
    string? Role,
    string Message,
    string? LaunchId = null,
    string? Topology = null,
    string? SaveId = null);

internal sealed record ReviewFixtureExecution(
    ReviewFixtureAccess Access,
    ReviewFixtureResult Result,
    ReviewFixtureProblem? Problem = null);

internal sealed record ReviewFixtureResult(
    bool Succeeded,
    string Message,
    ReviewFixtureStatusReport? Status = null,
    ReviewFixtureNavigationReport? Navigation = null,
    ReviewFixtureBuildingReport? Building = null,
    ReviewFixtureAnimalReport? Animal = null);

internal interface IReviewFixtureRuntime
{
    ReviewFixtureAccess VerifyExactReviewFixture();

    ReviewFixtureResult Status(ReviewFixtureAccess access);

    ReviewFixtureResult EnsureBuilding(
        ReviewFixtureAccess access,
        string alias,
        string kind,
        int x,
        int y);

    ReviewFixtureResult EnsureObject(
        ReviewFixtureAccess access,
        string building,
        string qualifiedItemId);

    ReviewFixtureResult ClearOwnedObjects(
        ReviewFixtureAccess access,
        string building);

    ReviewFixtureResult EnsureAnimal(
        ReviewFixtureAccess access,
        string building,
        string kind);

    ReviewFixtureResult Enter(
        ReviewFixtureAccess access,
        string building);

    ReviewFixtureResult Farm(ReviewFixtureAccess access);

    ReviewFixtureResult PrepareFishing(ReviewFixtureAccess access, bool skipTutorial);

    void BeginNavigation(
        ReviewFixtureAccess access,
        ReviewFixtureRequest request,
        Action<ReviewFixtureResult> completed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(completed);
        completed(request switch
        {
            ReviewFixtureEnterRequest enter => Enter(access, enter.Building),
            ReviewFixtureFarmRequest => Farm(access),
            _ => new ReviewFixtureResult(false, ReviewFixtureArguments.Usage),
        });
    }
}

internal static class ReviewFixtureOperation
{
    public static ReviewFixtureResult Execute(
        ReviewFixtureRequest request,
        IReviewFixtureRuntime runtime) =>
        ExecuteBound(request, runtime, expected: null).Result;

    public static ReviewFixtureExecution ExecuteBound(
        ReviewFixtureRequest request,
        IReviewFixtureRuntime runtime,
        ReviewFixtureRequestBinding? expected)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        ReviewFixtureExecution verification = VerifyBound(
            request.RequiresMainPlayer,
            runtime,
            expected);
        if (!verification.Result.Succeeded)
        {
            return verification;
        }

        ReviewFixtureAccess access = verification.Access;
        ReviewFixtureResult result = request switch
        {
            ReviewFixtureStatusRequest => runtime.Status(access),
            ReviewFixtureBuildingEnsureRequest building => runtime.EnsureBuilding(
                access,
                building.Alias,
                building.Kind,
                building.X,
                building.Y),
            ReviewFixtureObjectEnsureRequest item => runtime.EnsureObject(
                access,
                item.Building,
                item.QualifiedItemId),
            ReviewFixtureObjectClearOwnedRequest clear => runtime.ClearOwnedObjects(
                access,
                clear.Building),
            ReviewFixtureAnimalEnsureRequest animal => runtime.EnsureAnimal(
                access,
                animal.Building,
                animal.Kind),
            ReviewFixtureEnterRequest enter => runtime.Enter(access, enter.Building),
            ReviewFixtureFarmRequest => runtime.Farm(access),
            ReviewFixtureFishingRequest fishing => runtime.PrepareFishing(access, fishing.SkipTutorial),
            _ => new ReviewFixtureResult(false, ReviewFixtureArguments.Usage),
        };
        return new ReviewFixtureExecution(access, result);
    }

    public static ReviewFixtureExecution VerifyBound(
        bool requiresMainPlayer,
        IReviewFixtureRuntime runtime,
        ReviewFixtureRequestBinding? expected)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        ReviewFixtureAccess access = runtime.VerifyExactReviewFixture();
        if (!access.Succeeded)
        {
            return Failed(access, access.Message);
        }

        if (string.IsNullOrWhiteSpace(access.FixtureId)
            || string.IsNullOrWhiteSpace(access.Role))
        {
            return Failed(
                access,
                "The freshly verified review fixture returned an incomplete identity.");
        }

        if (expected is not null && !MatchesBinding(access, expected))
        {
            const string message =
                "The live review fixture identity changed after preflight; no fixture action was run.";
            return Failed(
                access,
                message,
                new ReviewFixtureProblem("fixtureBindingChanged", message));
        }

        if (requiresMainPlayer && !access.CanMutate)
        {
            return Failed(
                access,
                "Only the freshly verified main player or network-2 host may mutate the fixture.");
        }

        return new ReviewFixtureExecution(
            access,
            new ReviewFixtureResult(true, access.Message));
    }

    private static bool MatchesBinding(
        ReviewFixtureAccess access,
        ReviewFixtureRequestBinding expected) =>
        string.Equals(access.LaunchId, expected.LaunchId, StringComparison.Ordinal)
        && string.Equals(access.Topology, expected.Topology, StringComparison.Ordinal)
        && string.Equals(
            string.Equals(access.Role, ReviewFixtureTransportContract.SingleRoleToken, StringComparison.Ordinal)
                ? null
                : access.Role,
            expected.Role,
            StringComparison.Ordinal)
        && string.Equals(access.FixtureId, expected.FixtureId, StringComparison.Ordinal)
        && string.Equals(access.SaveId, expected.SaveId, StringComparison.Ordinal);

    private static ReviewFixtureExecution Failed(
        ReviewFixtureAccess access,
        string message,
        ReviewFixtureProblem? problem = null) =>
        new(access, new ReviewFixtureResult(false, message), problem);
}

internal static class ReviewFixtureNavigationOperation
{
    public static void ExecuteBound(
        ReviewFixtureRequest request,
        IReviewFixtureRuntime runtime,
        ReviewFixtureRequestBinding expected,
        Action<ReviewFixtureExecution> completed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(completed);

        if (request is not ReviewFixtureEnterRequest
            && request is not ReviewFixtureFarmRequest)
        {
            throw new ArgumentException(
                "Only fixture navigation requests can use deferred navigation.",
                nameof(request));
        }

        ReviewFixtureExecution verification = ReviewFixtureOperation.VerifyBound(
            request.RequiresMainPlayer,
            runtime,
            expected);
        if (!verification.Result.Succeeded)
        {
            completed(verification);
            return;
        }

        var completionClaimed = 0;
        void CompleteOnce(ReviewFixtureResult result)
        {
            if (Interlocked.Exchange(ref completionClaimed, 1) != 0)
            {
                return;
            }

            ReviewFixtureExecution completionVerification;
            try
            {
                completionVerification = ReviewFixtureOperation.VerifyBound(
                    request.RequiresMainPlayer,
                    runtime,
                    expected);
            }
            catch (Exception exception)
            {
                completed(new ReviewFixtureExecution(
                    verification.Access,
                    new ReviewFixtureResult(
                        false,
                        $"The fixture navigation completion check failed closed ({exception.GetType().Name}).")));
                return;
            }

            if (!completionVerification.Result.Succeeded)
            {
                if (string.Equals(
                        completionVerification.Problem?.Code,
                        "fixtureBindingChanged",
                        StringComparison.Ordinal))
                {
                    const string message =
                        "The live review fixture identity changed before navigation completed; "
                        + "the completed warp result cannot be attributed to the requested fixture.";
                    completionVerification = new ReviewFixtureExecution(
                        completionVerification.Access,
                        new ReviewFixtureResult(false, message),
                        new ReviewFixtureProblem("fixtureBindingChanged", message));
                }

                completed(completionVerification);
                return;
            }

            completed(new ReviewFixtureExecution(
                completionVerification.Access,
                result));
        }

        try
        {
            runtime.BeginNavigation(verification.Access, request, CompleteOnce);
        }
        catch (Exception exception)
        {
            CompleteOnce(new ReviewFixtureResult(
                false,
                $"The fixture navigation failed closed ({exception.GetType().Name})."));
        }
    }
}

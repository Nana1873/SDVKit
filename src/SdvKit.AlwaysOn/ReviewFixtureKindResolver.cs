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

internal sealed record ReviewFixtureKindResolution(
    string CanonicalId,
    string CanonicalToken);

internal static class ReviewFixtureKindResolver
{
    private const int CandidateLimit = 5;

    public static bool TryResolve(
        string input,
        IEnumerable<string> canonicalIds,
        string kindDescription,
        out ReviewFixtureKindResolution? resolution,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(canonicalIds);
        if (string.IsNullOrWhiteSpace(kindDescription))
        {
            throw new ArgumentException(
                "A kind description is required.",
                nameof(kindDescription));
        }

        resolution = null;
        string normalizedInput = Normalize(input);
        if (normalizedInput.Length == 0)
        {
            error = $"The {kindDescription} kind '{input}' has no stable token characters.";
            return false;
        }

        (string CanonicalId, string Token)[] candidates = canonicalIds
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && id.Length <= 128
                && !id.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .Select(id => (CanonicalId: id, Token: Normalize(id)))
            .Where(candidate => candidate.Token.Length > 0)
            .ToArray();
        (string CanonicalId, string Token)[] matches = candidates
            .Where(candidate => string.Equals(
                candidate.Token,
                normalizedInput,
                StringComparison.Ordinal))
            .Take(CandidateLimit + 1)
            .ToArray();
        if (matches.Length == 1)
        {
            resolution = new ReviewFixtureKindResolution(
                matches[0].CanonicalId,
                matches[0].Token);
            error = string.Empty;
            return true;
        }

        if (matches.Length > 1)
        {
            error = $"The {kindDescription} kind '{input}' is ambiguous: "
                + string.Join(
                    ", ",
                    matches.Take(CandidateLimit).Select(DescribeCandidate))
                + ".";
            return false;
        }

        string[] suggestions = candidates
            .OrderBy(candidate => EditDistance(normalizedInput, candidate.Token))
            .ThenBy(candidate => candidate.Token, StringComparer.Ordinal)
            .Take(CandidateLimit)
            .Select(DescribeCandidate)
            .ToArray();
        error = $"Stardew's loaded data has no unambiguous {kindDescription} kind '{input}'."
            + (suggestions.Length == 0
                ? string.Empty
                : $" Canonical candidates: {string.Join(", ", suggestions)}.");
        return false;
    }

    public static string Normalize(string value) =>
        StableIdentityNormalizer.Normalize(value);

    private static string DescribeCandidate((string CanonicalId, string Token) candidate) =>
        string.Equals(candidate.CanonicalId, candidate.Token, StringComparison.Ordinal)
            ? candidate.Token
            : $"{candidate.Token} ('{candidate.CanonicalId}')";

    private static int EditDistance(string left, string right)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                int substitution = previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

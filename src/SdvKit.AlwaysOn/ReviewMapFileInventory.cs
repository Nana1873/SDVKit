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

internal static class ReviewMapFileInventory
{
    private static readonly Regex LocaleSuffix = new(
        @"\.[a-z]{2}(?:-[a-z]{2})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal const int MaximumVisitedEntries = ReviewMapContract.MaximumDiscoveredAssets * 4;

    public static IReadOnlyList<string> Discover(
        string contentRoot,
        string mapRoot,
        int maximumVisitedEntries = MaximumVisitedEntries,
        Func<string, bool>? isLocalizedAsset = null)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVisitedEntries, 1);
#else
        if (maximumVisitedEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVisitedEntries));
        }
#endif

        RefuseReparsePoint(mapRoot);
        var names = new List<string>();
        var pending = new Stack<string>();
        var visitedEntries = 0;
        pending.Push(mapRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                visitedEntries++;
                if (visitedEntries > maximumVisitedEntries)
                {
                    throw new InvalidDataException(
                        "The installed Maps asset tree exceeds its bounded entry maximum.");
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The installed Maps asset tree contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(entry), ".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(contentRoot, entry).Replace('\\', '/');
                string assetName = relative[..^Path.GetExtension(relative).Length];
                bool localized = isLocalizedAsset?.Invoke(assetName)
                    ?? LocaleSuffix.IsMatch(assetName);
                if (!localized)
                {
                    names.Add(assetName);
                    if (names.Count > ReviewMapContract.MaximumDiscoveredAssets)
                    {
                        return names;
                    }
                }
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void RefuseReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The installed Maps asset root is not a regular directory.");
        }
    }
}

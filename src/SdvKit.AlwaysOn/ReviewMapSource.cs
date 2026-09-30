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

internal interface IReviewMapSource
{
    string GameVersion { get; }

    string GameFileVersion { get; }

    IReadOnlyList<string> DiscoverCanonicalAssetNames();

    ReviewMapAssetIdentity CanonicalizeAssetName(string assetName);

    bool AssetExistsForMapRequest(string assetName);

    ReviewMapLoadedAsset LoadAsset(string assetName);

    ReviewMapLoadedAsset LoadMapAsset(string assetName);

    ReviewMapTileSnapshot ReadTile(string assetName, string layerId, int x, int y);
}

internal sealed record ReviewMapAssetIdentity(
    string Name,
    string BaseName,
    string? LocaleCode);

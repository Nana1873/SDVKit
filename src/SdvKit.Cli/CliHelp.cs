using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string InspectUsage = "Usage: sdvkit project inspect [path] --json";
    private const string CheckUsage = "Usage: sdvkit project check [path] [--json]";
    private const string CreateUsage = "Usage: sdvkit project create <smapi-mod|content-pack> <path> --name <name> --author <author> --unique-id <id> --description <text> --json";
    private const string BuildUsage = "Usage: sdvkit project build [path] [--project <relative.csproj>] [--game-path <directory>] --json";
    private const string PackageUsage = "Usage: sdvkit project package [path] [--project <relative.csproj>] [--game-path <directory>] --json";
    private const string SmokeUsage =
        "Usage: sdvkit project smoke [path] [--game-path <directory>] --topology <single|network-2> --json";
    private const string ReviewStartUsage =
        "Usage: sdvkit project review start [source-project-or-ready-mod] [--project <relative.csproj>] [--game-path <directory>] [--topology <single|network-2>] [--test-save] [--companion <path>]... [--content-pack <path>]... --json";
    private const string ReviewStatusUsage =
        "       sdvkit project review status [--topology <single|network-2>] --json";
    private const string ReviewCommandUsage =
        "       sdvkit project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json";
    private const string ReviewStopUsage =
        "       sdvkit project review stop [--topology <single|network-2>] --json";
    private const string ReviewResetUsage =
        "       sdvkit project review reset --topology <single|network-2> --json";
    private const string ReviewDataAssetsUsage =
        "       sdvkit project review data assets [--offset <n>] [--limit <1-100>] [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";
    private const string ReviewDataKeysUsage =
        "       sdvkit project review data keys <asset> [--offset <n>] [--limit <1-100>] [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";
    private const string ReviewDataGetUsage =
        "       sdvkit project review data get <asset> <key> [--topology <single|network-2>] [--role <host|farmhand>] [--screen <id>] --json";
    private const string ReviewMapAssetsUsage =
        "       sdvkit project review map assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapGetUsage =
        "       sdvkit project review map get <map> [--topology single] --json";
    private const string ReviewMapLayersUsage =
        "       sdvkit project review map layers <map> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapLayerUsage =
        "       sdvkit project review map layer <map> <layer> [--topology single] --json";
    private const string ReviewMapTileSheetsUsage =
        "       sdvkit project review map tilesheets <map> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapWarpsUsage =
        "       sdvkit project review map warps <map> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapTileUsage =
        "       sdvkit project review map tile <map> <layer> <x> <y> [--topology single] --json";
    private const string ReviewMapPropertyMapUsage =
        "       sdvkit project review map property <map> map <property> [--topology single] --json";
    private const string ReviewMapPropertyLayerUsage =
        "       sdvkit project review map property <map> layer <layer> <property> [--topology single] --json";
    private const string ReviewMapPropertyTileUsage =
        "       sdvkit project review map property <map> tile <layer> <x> <y> direct <property> [--topology single] --json";
    private const string ReviewMapPropertyIndexUsage =
        "       sdvkit project review map property <map> tile <layer> <x> <y> tile-index <property> [--frame <n>] [--topology single] --json";
    private const string ReviewTextureAssetsUsage =
        "       sdvkit project review texture assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewTextureGetUsage =
        "       sdvkit project review texture get <asset> [--topology single] --json";
    private const string ReviewTexturePreviewUsage =
        "       sdvkit project review texture preview <asset> [--topology single] --json";
    private const string ReviewAudioCuesUsage =
        "       sdvkit project review audio cues [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewAudioCueUsage =
        "       sdvkit project review audio cue <id> [--topology single] --json";
    private const string ReviewModAssetAssetsUsage =
        "       sdvkit project review mod-assets assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewModAssetKeysUsage =
        "       sdvkit project review mod-assets keys <Mods/owner/asset> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewModAssetGetUsage =
        "       sdvkit project review mod-assets get <Mods/owner/asset> <key> [--topology single] --json";
    private const string ReviewMcpSingleUsage =
        "       sdvkit project review mcp serve [--topology single] [--allow-input] [--allow-fixture-actions] [--allow-world-actions] [--allow-container-transfer] [--allow-cp-refresh]";
    private const string ReviewMcpScreenUsage =
        "       sdvkit project review mcp serve [--topology single] --screen <id> [--allow-input]";
    private const string ReviewMcpNetworkUsage =
        "       sdvkit project review mcp serve --topology network-2 --role <host|farmhand> [--allow-input] [--allow-fixture-actions]";
    private const string ReviewMcpToolsDescription =
        "       all MCP topologies/selections: stardew_runtime_get, stardew_review_get, stardew_mods_list, stardew_mod_diagnostics, stardew_menu_get, stardew_screenshot_capture, stardew_inventory_get, stardew_container_get, stardew_world_area_get, stardew_data_assets_list, stardew_data_keys_list, stardew_data_record_get; unbound single additionally: stardew_map_assets_list, stardew_map_get, stardew_map_layers_list, stardew_map_layer_get, stardew_map_tilesheets_list, stardew_map_warps_list, stardew_map_tile_get, stardew_map_property_get, stardew_texture_assets_list, stardew_texture_get, stardew_texture_preview, stardew_audio_cues_list, stardew_audio_cue_get, stardew_mod_assets_list, stardew_mod_asset_keys_list, stardew_mod_asset_record_get, stardew_shop_get, stardew_progression_get (owned disposable single only), stardew_cp_diagnose";
    private const string ReviewMcpInputDescription =
        "       --allow-input additionally exposes only: stardew_input_press, stardew_input_chord, stardew_input_text, stardew_input_click, stardew_input_scroll, stardew_input_drag, stardew_input_cursor_set, stardew_input_cursor_clear, stardew_input_wheel; screen-bound servers omit shared-window text";
    private const string ReviewMcpFixtureDescription =
        "       --allow-fixture-actions additionally exposes only: stardew_fixture_status_get, stardew_fixture_enter, stardew_fixture_farm, stardew_fixture_building_ensure, stardew_fixture_animal_ensure, stardew_fixture_save as allowed for the selected role; unsupported with --screen";
    private const string ReviewMcpWorldActionDescription =
        "       --allow-world-actions additionally exposes only: stardew_world_interact for the exact owned disposable single-player review";
    private const string ReviewMcpContainerTransferDescription =
        "       --allow-container-transfer additionally exposes only: stardew_container_transfer for the exact owned unbound disposable single-player review";
    private const string LabSingleUsage =
        "Usage: sdvkit lab <start|status|stop|test-save> --topology single --json";
    private const string LabNetworkTwoUsage =
        "       sdvkit lab smoke [--game-path <directory>] --topology network-2 --json";

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("SDVKit — Stardew Valley modding toolkit and isolated live test lab");
        output.WriteLine();
        output.WriteLine("Usage: sdvkit <command> [options]");
        output.WriteLine("  version [--json]  Show the installed version.");
        output.WriteLine("  doctor --json     Discover a ready game and SMAPI installation.");
        output.WriteLine("  project --help    Create, inspect, check, build, package, smoke-test, or review a mod.");
        output.WriteLine("  save --help       Inspect an explicitly selected isolated save copy.");
        output.WriteLine("  lab --help        Control the isolated live lab and disposable world.");
        output.WriteLine();
        output.WriteLine("Use each subcommand's --help for syntax and supported options.");
        output.WriteLine("First use: doctor -> project create/inspect -> check/build/package -> review start/status -> diagnose/observe -> stop/reset.");
        output.WriteLine($"Documentation: https://github.com/Nana1873/SDVKit/tree/v{CurrentVersion}#readme");
        output.WriteLine($"Support and upgrades: https://github.com/Nana1873/SDVKit/blob/v{CurrentVersion}/docs/support.md");
    }

    private static void WriteLabUsage(TextWriter output)
    {
        output.WriteLine(LabSingleUsage);
        output.WriteLine(LabNetworkTwoUsage);
        output.WriteLine("--game-path <directory> selects a complete installation for start, test-save, or smoke only.");
    }

    private static void WriteProjectHelp(TextWriter output)
    {
        output.WriteLine("SDVKit project toolkit");
        output.WriteLine();
        output.WriteLine(InspectUsage);
        output.WriteLine(CheckUsage);
        output.WriteLine(CreateUsage);
        output.WriteLine(BuildUsage);
        output.WriteLine(PackageUsage);
        output.WriteLine(SmokeUsage);
        output.WriteLine("       sdvkit project review --help");
        output.WriteLine();
        output.WriteLine("Smoke checks loading and bounded game ticks for one standalone C# mod.");
        output.WriteLine("Review keeps a C# source or extracted ready code mod, or a single-player content pack, running for functional checks.");
    }

    private static void WriteProjectReviewUsage(TextWriter output)
    {
        output.WriteLine(ReviewStartUsage);
        output.WriteLine(ReviewStatusUsage);
        output.WriteLine(ReviewCommandUsage);
        output.WriteLine(ReviewStopUsage);
        output.WriteLine(ReviewResetUsage);
        output.WriteLine();
        output.WriteLine("Reference help:");
        output.WriteLine("  sdvkit project review command --help     Console input, screenshots, fixtures, and network lifecycle.");
        output.WriteLine("  sdvkit project review data --help        Canonical structured Data.");
        output.WriteLine("  sdvkit project review menu --help        Read-only active menus for the selected role.");
        output.WriteLine("  sdvkit project review shop --help        Read-only SeedShop Gold evidence (single only).");
        output.WriteLine("  sdvkit project review progression --help Selected NPC/mail/quest observations (owned disposable single only).");
        output.WriteLine("  sdvkit project review inventory --help   Read-only selected-player bounded backpack.");
        output.WriteLine("  sdvkit project review world --help       Read-only selected-role crop, soil, and machine area.");
        output.WriteLine("  sdvkit project review container --help   Read-only selected-role regular vanilla chest.");
        output.WriteLine("  sdvkit project review container-transfer --help  Exact bounded native chest transfer (unbound single only).");
        output.WriteLine("  sdvkit project review interact --help    One revision-bound native world action (owned test save only).");
        output.WriteLine("  sdvkit project review map --help         Map structure and properties.");
        output.WriteLine("  sdvkit project review texture --help     Texture metadata and diagnostic previews.");
        output.WriteLine("  sdvkit project review audio --help       Audio metadata without playback.");
        output.WriteLine("  sdvkit project review mod-assets --help  Observed mod asset namespaces.");
        output.WriteLine("  sdvkit project review diagnostics --help Selected-mod warnings and exceptions.");
        output.WriteLine("  sdvkit project review cp-diagnose --help Selected Content Patcher diagnosis and asset patch order.");
        output.WriteLine("  sdvkit project review cp-refresh --help Refresh selected owned CP patch JSON.");
        output.WriteLine("  sdvkit project review config-reconcile --help Accept a selected staged config change.");
        output.WriteLine("  sdvkit project review mcp serve --help   Role-bound STDIO tools and action opt-ins.");
        output.WriteLine();
        output.WriteLine("Content-pack targets require --topology single and an explicit provider --companion.");
        output.WriteLine("Ready bundles support single: select their root and every direct pack child with --content-pack; exactly one root/direct-child code mod is required.");
        output.WriteLine("Map, texture, audio and mod-asset inspection require unbound single; Data and menu inspection support exact roles and local screens.");
        output.WriteLine("Local split-screen: in a single --test-save review, send sdvkit split-screen join/leave/status; use native screen=<id> console selection.");
    }

    private static void WriteProjectReviewMcpUsage(TextWriter output)
    {
        output.WriteLine(ReviewMcpSingleUsage.TrimStart());
        output.WriteLine(ReviewMcpScreenUsage.TrimStart());
        output.WriteLine(ReviewMcpNetworkUsage.TrimStart());
        output.WriteLine(ReviewMcpToolsDescription.TrimStart());
        output.WriteLine(ReviewMcpInputDescription.TrimStart());
        output.WriteLine(ReviewMcpFixtureDescription.TrimStart());
        output.WriteLine(ReviewMcpWorldActionDescription.TrimStart());
        output.WriteLine(ReviewMcpContainerTransferDescription.TrimStart());
        output.WriteLine("--allow-cp-refresh separately exposes stardew_cp_refresh for the exact startup single root CP 2.9.1 pack; input and fixture opt-ins never authorize refresh.");
        output.WriteLine("Start the review through the CLI first, then serve from the same lab directory.");
        output.WriteLine("The role or exact observed local screen and farmer context is fixed at startup. A departed/rejoined screen requires a new server. Closing stdin stops this server; stdout contains only MCP frames.");
    }

    private static void WriteProjectReviewDataUsage(TextWriter output)
    {
        output.WriteLine(ReviewDataAssetsUsage.TrimStart());
        output.WriteLine(ReviewDataKeysUsage.TrimStart());
        output.WriteLine(ReviewDataGetUsage.TrimStart());
        output.WriteLine(
            "Queries require the selected active owned review and return process-shared canonical installed Data assets after the active SMAPI content pipeline; --screen validates the exact local binding but does not create a screen-local Data cache.");
        output.WriteLine(
            "For an operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewMapUsage(TextWriter output)
    {
        output.WriteLine(ReviewMapAssetsUsage.TrimStart());
        output.WriteLine(ReviewMapGetUsage.TrimStart());
        output.WriteLine(ReviewMapLayersUsage.TrimStart());
        output.WriteLine(ReviewMapLayerUsage.TrimStart());
        output.WriteLine(ReviewMapTileSheetsUsage.TrimStart());
        output.WriteLine(ReviewMapWarpsUsage.TrimStart());
        output.WriteLine(ReviewMapTileUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyMapUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyLayerUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyTileUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyIndexUsage.TrimStart());
        output.WriteLine(
            "Property scopes: map <name>; layer <layer> <name>; tile <layer> <x> <y> <direct|tile-index> <name> (animated tile-index requires --frame <n>). Queries require an active owned single review.");
        output.WriteLine(
            "For a map, layer, or property operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewTextureUsage(TextWriter output)
    {
        output.WriteLine(ReviewTextureAssetsUsage.TrimStart());
        output.WriteLine(ReviewTextureGetUsage.TrimStart());
        output.WriteLine(ReviewTexturePreviewUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review. Inventory is canonical and measured; exact metadata and one bounded diagnostic PNG reflect the final SMAPI content pipeline without claiming per-mod provenance.");
        output.WriteLine(
            "For an asset operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewAudioUsage(TextWriter output)
    {
        output.WriteLine(ReviewAudioCuesUsage.TrimStart());
        output.WriteLine(ReviewAudioCueUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review. Discovery covers the final Data/AudioChanges and Data/JukeboxTracks populations; the public API cannot enumerate the built-in XACT cue bank.");
        output.WriteLine(
            "For a cue operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewModAssetUsage(TextWriter output)
    {
        output.WriteLine(ReviewModAssetAssetsUsage.TrimStart());
        output.WriteLine(ReviewModAssetKeysUsage.TrimStart());
        output.WriteLine(ReviewModAssetGetUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review and cover only observed requests in canonical Mods/<owner>/... namespaces. Six explicit primitive adapters are supported; detailed provider attribution is unavailable through the public SMAPI API.");
        output.WriteLine(
            "For an asset or key operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteReviewFixtureConsoleUsage(TextWriter output)
    {
        output.WriteLine(
            "AlwaysOn review console lines (quote one as <text> for project review command; not top-level CLI):");
        output.WriteLine("  sdvkit split-screen join|leave|status (single --test-save review; append native screen=<id> to select a local screen)");
        output.WriteLine("  sdvkit network leave|join|status (network-2; leave/join are farmhand-only and host-authorized)");
        output.WriteLine("  sdvkit screenshot <label>");
        output.WriteLine("  sdvkit screenshot viewport <label>");
        output.WriteLine("  sdvkit input press <SButton>");
        output.WriteLine("  sdvkit input chord <1-120 ticks> <menu uiRevision> <1-8 SButton names>");
        output.WriteLine("  sdvkit input click <x> <y> <mouse-button> <1|2> <uiRevision> [modifiers...]");
        output.WriteLine("  sdvkit input scroll <x> <y> <-20..-1|1..20 notches> <uiRevision>");
        output.WriteLine("  sdvkit input drag <x> <y> <end-x> <end-y> <mouse-button> <1-120 movement updates> <uiRevision> [modifiers...]");
        output.WriteLine("  sdvkit input cursor <ui-x> <ui-y>");
        output.WriteLine("  sdvkit input cursor clear");
        output.WriteLine("  sdvkit fixture status");
        output.WriteLine("  sdvkit fixture building ensure <alias> <building-kind> <x> <y>");
        output.WriteLine("  sdvkit fixture object ensure <alias-or-id> <qualified-item-id>");
        output.WriteLine("  sdvkit fixture object clear-owned <alias-or-id>");
        output.WriteLine("  sdvkit fixture animal ensure <alias-or-id> <animal-kind>");
        output.WriteLine(
            "  Kinds resolve from loaded canonical Stardew data IDs; legacy deluxe-barn and white-cow remain valid.");
        output.WriteLine(
            "  Unknown, ambiguous, unplaceable, or animal-house-incompatible kinds fail before mutation.");
        output.WriteLine("  sdvkit fixture enter <alias-or-id>");
        output.WriteLine("  sdvkit fixture enter greenhouse");
        output.WriteLine("  sdvkit fixture farm");
    }
}

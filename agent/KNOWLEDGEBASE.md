# SMAPI 4 and Stardew 1.6 pitfalls

Written by SDVKit {{version}} with `sdvkit lab init`, which keeps this file current; put your own notes elsewhere. Each entry is a trap that compiles or loads cleanly and then misbehaves. Every section names its primary source; when a source and this file disagree, the source wins, and the game version you test against is what counts.

## Item IDs and data assets (Stardew 1.6)

Source: [Migrate to Stardew Valley 1.6](https://stardewvalleywiki.com/Modding:Migrate_to_Stardew_Valley_1.6).

- Item IDs are strings. Compare `QualifiedItemId` (`"(O)128"`, `"(BC)128"`), not `ParentSheetIndex`: the same unqualified ID can name an object and a big craftable. Create items with `ItemRegistry.Create("(O)128")` and check types with `item.HasTypeId(ItemRegistry.type_object)`.
- New IDs should start with your mod's unique ID and use only letters, digits, underscores and dots; in Content Patcher, `"{{ModId}}_MyItem"`.
- Some fields still take unqualified IDs, for example recipe outputs (with a separate big-craftable flag) and fruit tree saplings. Check the asset's own format before qualifying.
- Old slash-delimited assets were replaced by data models: `Data/ObjectInformation` became `Data/Objects`, `Data/BigCraftablesInformation` became `Data/BigCraftables`, `Data/ClothingInformation` became `Data/Shirts` and `Data/Pants`, `Data/NPCDispositions` became `Data/Characters`, `Data/Blueprints` became `Data/Buildings`. `Data/ObjectContextTags` is gone; tags live in each entry's `ContextTags`. Patches to old names silently target nothing.
- In C#, `Game1.objectInformation` and friends became `Game1.objectData`, `Game1.bigCraftableData`, `Game1.shirtData` and `Game1.pantsData`. Prefer `DataLoader.<Asset>(Game1.content)` over `Game1.content.Load<...>("Data\\...")` with a hand-written type.
- Use exact asset names such as `Data/Objects`: names are case-sensitive on Linux and macOS, even when Windows forgives them.

## Changed game APIs (Stardew 1.6)

Source: the same migration page, "Breaking changes for C# mods".

- `Farmer.mailReceived`, `Farmer.eventsSeen` and similar fields are hash sets now; index access no longer compiles. Use `Contains` and `Add`.
- `Farmer.items` and chest items use the `Inventory` class. `getItemCount`, `hasItemInInventory` and `removeItemsFromInventory` were replaced by `CountId`, `ContainsId` and `ReduceId`.
- `Game1.weather` and `weatherForTomorrow` are strings; compare against `Game1.weather_*` constants, not old numbers.
- `getTileX()` and `getStandingX()` became `Tile` and `StandingPixel`; `isMarried()` became `isMarriedOrRoommates()`; stat fields such as `stats.dirtHoed` became properties, and `getStat`/`incrementStat` became `Get`/`Increment`.
- Most `Crop` fields moved to `crop.GetData()`. `HoeDirt.fertilizer` is `null` when empty.
- Members kept only for save migration are marked obsolete. Treat every "is obsolete" build warning as a bug.

## SMAPI 4 content API

Source: [Migrate to SMAPI 4.0](https://stardewvalleywiki.com/Modding:Migrate_to_SMAPI_4.0) and [Content API](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Content).

- `helper.Content`, `IAssetLoader` and `IAssetEditor` are gone. Load your own files with `helper.ModContent` (with the file extension, not cached) and game assets with `helper.GameContent` (without an extension, cached). Edit or provide assets in the `Content.AssetRequested` event.
- Asset names include the locale. Match with `e.NameWithoutLocale.IsEquivalentTo("Data/Objects")`, or your edit misses non-English players.
- `e.LoadFrom(...)` needs an `AssetLoadPriority`. When several mods load the same asset, only one load wins; if more than one uses `Exclusive`, SMAPI logs an error and applies none. Prefer `e.Edit(...)`.
- After changing what you provide for an already loaded asset, call `helper.GameContent.InvalidateCache(...)` for it, or the game keeps the cached copy. It is expensive; never call it every tick.

## Events and lifecycle

Source: [Events](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Events) and [Mod structure](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Mod_structure).

- In `Entry`, read config and subscribe to events only. No save, farmer or location exists yet.
- Set up integrations with other mods' APIs (Generic Mod Config Menu, Content Patcher tokens) in `GameLoop.GameLaunched`, after all mods have loaded.
- `SaveLoaded` runs after a save loads or a farmhand connects, before `DayStarted`. `DayStarted` is the usual place for daily logic.
- Input and tick events also run at the title screen. Check `Context.IsWorldReady` before touching `Game1.player` or locations.
- `UpdateTicked` runs about 60 times per second; throttle with `e.IsMultipleOf(n)`.
- Clear per-save state in `ReturnedToTitle`, or it leaks into the next save.

## Harmony

Source: [Harmony](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Harmony).

- Harmony is a last resort. First check whether a SMAPI event, the input API or a content edit does the job.
- Enable it with `<EnableHarmony>true</EnableHarmony>` and create one instance with `new Harmony(this.ModManifest.UniqueID)`. Find targets with `AccessTools.Method(typeof(T), nameof(T.Method))` so renames break the build, not the game.
- Prefer postfixes. A prefix that returns `false` skips the original and can break other mods' patches. Avoid transpilers unless nothing else works.
- Wrap patch bodies in `try`/`catch`, log the exception with `LogLevel.Error`, and let the original run (`return true` from a prefix).
- The JIT can inline small methods; a patch on an inlined method never fires. When a patch "does nothing", suspect that before your logic.
- SMAPI warns that Harmony mods "may affect game stability". Expect that warning in the log; it is not an error from your code.

## Multiplayer and split-screen

Source: [Multiplayer API](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Multiplayer).

- Only the host has every location. A farmhand gets its current location, the farm, the farmhouse and farm buildings. Code that walks all locations must run on the host (`Context.IsMainPlayer`) or tolerate missing ones.
- Shared world changes belong to the host. Farmhands send a request with `helper.Multiplayer.SendMessage(...)`; the host applies it. Your own mod instance does not receive messages it sent.
- In `ModMessageReceived`, check `e.FromModID` and `e.Type` before `e.ReadAs<T>()`. Message classes need a parameterless constructor.
- In split-screen, one mod instance runs for every local player. Keep per-player state in `readonly PerScreen<T>` fields and set `.Value`; a plain static field is shared by all screens.
- `GetConnectedPlayers()` can include players that are still joining; `Game1.getOnlineFarmers()` lists only connected farmers. `peer.GetMod(id)` tells you whether a peer has your mod.
- Test both roles. SDVKit's `network-2` topology runs a host and a farmhand: skill `sdv-multiplayer`.

## Translations

Source: [Translation API](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Translation) and SDVKit's [i18n guide]({{docs}}/i18n-authoring.md).

- Put text in `i18n/default.json` plus one file per language, such as `i18n/de.json`. A missing key falls back from `pt-BR.json` to `pt.json` to `default.json`.
- A key missing everywhere does not throw. SMAPI returns the placeholder `(no translation:<key>)`, which then shows up in the game. Search screenshots and logs for `no translation`.
- Keys are case-insensitive. Pass tokens as an object: `helper.Translation.Get("key", new { name })`.
- Do not cache translated text at startup; the player can change the language. Translate when you draw or build the menu.

## Content Patcher

Source: [Content Patcher author guide](https://github.com/Pathoschild/StardewMods/blob/develop/ContentPatcher/docs/author-guide.md), its [EditData](https://github.com/Pathoschild/StardewMods/blob/develop/ContentPatcher/docs/author-guide/action-editdata.md) and [tokens](https://github.com/Pathoschild/StardewMods/blob/develop/ContentPatcher/docs/author-guide/tokens.md) pages, and SDVKit's [CP diagnosis]({{docs}}/cp-diagnosis.md).

- `Format` is the Content Patcher version you wrote the pack for. Use the latest; an old `Format` makes Content Patcher apply legacy behavior and migrations. SDVKit's bundled schema checks `2.9.x`, and `project check` rejects other versions.
- Prefer `EditData` with `Fields` (change a few fields of an entry) over `Entries` (replace an entry), and both over `Load`. A `Load` replaces the whole asset and runs before every edit.
- Delete an entry with `"key": null`. Drill into a nested field, such as one item's `ContextTags`, with `TargetField`.
- A patch whose tokens are unavailable is disabled without an error. Many tokens, such as `{{Season}}`, are unavailable until a save is loaded, so a review at the title screen shows misleading patch states. Diagnose with `--test-save` (skill `sdv-content-patcher`).
- Patches refresh at day start by default. Conditions that change during the day need `"Update": "OnLocationChange"` or `"OnTimeChange"`.
- Token names and condition values are case-insensitive; asset names and file paths are not, on Linux and macOS.
- Tokens provided by another mod work only when that mod is a required dependency, or when the patch also has an immutable `HasMod` condition for it.
- Use `Priority` (`Early`, `Default`, `Late`) only when another mod's patch genuinely has to run first; it makes conflicts harder to trace.
- Do not run `patch export` in the lab: it writes into the game folder. SDVKit's `project review cp-diagnose` runs `patch summary` and `patch parse` for you, and `project review data get` reads the final asset.

## Generic Mod Config Menu

Source: SDVKit's [GMCM guide]({{docs}}/gmcm-authoring.md), which carries the checked API interface.

- Get the API in `GameLaunched` with `Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu")`. It is `null` when GMCM is not installed; your mod must still work then.
- Copy only the methods you call into your own interface, with GMCM's exact signatures; a method GMCM does not have, or a mismatched one, makes `GetApi` fail.
- `Register(..., save: () => Helper.WriteConfig(Config), titleScreenOnly: false)` when the options should also work in a loaded save. Without `WriteConfig` in `save`, changes vanish on restart.
- A ticked checkbox is not proof of a saved value. Click Save, then read `config.json` or the mod's behavior (skill `sdv-gmcm`).

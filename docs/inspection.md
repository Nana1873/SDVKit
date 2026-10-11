# Inspect active game content

Use these commands to read the final SMAPI content pipeline of a running review. SMAPI must have confirmed that the reviewed mod loaded. Data reads work in `single`, for one `network-2` role, or for one split-screen screen. The other asset families work only in `single`. Start with the [live guide](live-review.md#start-a-review). Examples use the executable variable from [installation](../README.md#install).

Choose [Data](#read-canonical-data-definitions), [maps](#inspect-active-map-structure), [textures](#inspect-canonical-textures-safely), [audio](#inspect-active-audio-metadata), or [observed mod assets](#inspect-observed-mod-owned-asset-namespaces). All five families are also available through [MCP](mcp.md). The MCP map and texture tools add typed metadata and a checked preview. The MCP audio and mod-asset tools keep the CLI selections and limits.

## Shared rules

- Require exit `0`, the identity you asked for, and a result from the current review.
- Follow pagination. The first page is not the whole list.
- A failed or partial inventory is not complete coverage.
- Save diagnostic previews only below the role's isolated profile.
- If an operand starts with `-` or matches an option name, put all options before `--` and all operands after it.
- Unknown identities, unsupported shapes, ambiguous names, stale responses and size limits return controlled problems. SDVKit does not guess a replacement ("fails closed"). Do not guess one yourself, and do not retry an unchanged request that timed out.
- Every command is read-only and [bounded](glossary.md#commands-and-results). None of them mutates the game, exports source files or reads your normal `Mods` folder.

## Read canonical Data definitions

The `data` subcommands read every structured `Data/*` asset that the installed Stardew version ships. SDVKit finds the asset names in the installed `Content/Data` folder; it keeps no allowlist. It loads each asset through SMAPI's live content pipeline. Results therefore include the content packs and edits active in this review. Localized copies of an asset are not separate entries.

```powershell
& $sdvkit project review data assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review data keys "Data/Buildings" --offset 0 --limit 50 --topology single --json
& $sdvkit project review data get "Data/Buildings" "Barn" --topology single --json
& $sdvkit project review data get "Data/Buildings" "Barn" --topology network-2 --role farmhand --json
& $sdvkit project review data get "Data/Buildings" "Barn" --topology single --screen 1 --json
```

- `assets` reports the running game and file versions. For each asset it reports the [canonical name](glossary.md#assets), the loaded .NET type, the shape and the key kind. Coverage is complete only with zero `unknown`, `unclassified` and `unsupported` entries.
- Keys: dictionaries use their string or integer keys. Lists use zero-based indexes. A singleton has the one key `singleton`.
- `keys` returns keys in a stable order. Keys are not replaced by localized display values.
- `get` returns one record with its asset and key. JSON object members are sorted. Arrays keep their source order.
- Asset and string-key lookup tolerates case differences and small separator differences (space, hyphen, underscore, slash, backslash). If two names collide after this, the lookup fails as ambiguous. The exact key still works.
- A `Data/*` name that the running game version lacks is reported as unavailable. A name outside `Data/` is reported separately.
- Load failures, unsupported key types, unsafe or oversized records and mismatched requests return problems.
- Pages default to 50 entries and accept 1-100. Offsets are non-negative. There is no full dump.

## Inspect active map structure

The `map` subcommands inspect map assets through SMAPI's active content pipeline in a load-confirmed `single` review. They do not return a whole tile matrix.

```powershell
& $sdvkit project review map assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review map get "Maps/Town" --topology single --json
& $sdvkit project review map layers "Maps/Town" --offset 0 --limit 50 --topology single --json
& $sdvkit project review map layer "Maps/Town" "Buildings" --topology single --json
& $sdvkit project review map tilesheets "Maps/Town" --offset 0 --limit 50 --topology single --json
& $sdvkit project review map warps "Maps/Town" --offset 0 --limit 50 --topology single --json
& $sdvkit project review map tile "Maps/Town" "Back" 10 12 --topology single --json
& $sdvkit project review map property "Maps/Town" map "Outdoors" --topology single --json
```

Run `& $sdvkit project review map --help` for the layer, direct-tile and tile-index property forms. Pass only property names and coordinates that exist in the map. SDVKit does not guess them. A missing selection returns a `blocked` result.

- `assets` lists the map files found in the installed `Content/Maps` folder, without locale copies. Each is a supported xTile map, a known non-map, or a gap. Require `coverage.complete=true` before you claim complete coverage.
- This inventory lists game files only. A `Maps/*` name that a loaded mod adds can still be inspected by name.
- A map name the pipeline cannot provide is reported separately from a name outside `Maps/`. Load failures, name collisions, malformed warps, unsafe shapes and oversized structures return problems.
- List commands are paged in a stable order. `get`, `layer`, `tile` and `property` return one selection.
- Warps distinguish general `Warp` routes (`playerAndNpc`) from NPC-only `NPCWarp` routes (`npc`).
- `tile` reports an empty, static or animated tile, with frame references and property counts.
- `property` keeps the value's JSON type. It needs a scope: map, layer, direct tile or tile index. A property on an animated tile index also needs its zero-based `--frame`. Direct-tile and tile-index properties stay separate.

The [single-review MCP adapters](mcp.md#maps-and-textures) use the same selections and limits.

## Inspect canonical textures safely

The `texture` subcommands work in the same load-confirmed `single` review. They cover the non-localized `Content/**/*.xnb` files.

```powershell
& $sdvkit project review texture assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review texture get "LooseSprites/Cursors" --topology single --json
& $sdvkit project review texture preview "LooseSprites/Cursors" --topology single --json
```

- `assets` classifies each file by its XNB reader without loading the asset. The built-in `Texture2DReader` means texture. A short list of built-in data, collection, map, font and effect readers means non-texture. Any malformed, custom or unknown reader is a gap. Pages list only textures. The coverage object counts all candidates, textures, non-textures and gaps.
- `get` loads one texture through the active pipeline. It returns dimensions, runtime format, mip level count, game versions and availability. Case and separator differences are tolerated when the name stays unambiguous. The result names the game's spelling of the asset.
- Provenance: SMAPI's public API does not reliably say which mod loaded or edited a final texture. The typed provenance object therefore reports `final-post-pipeline` and marks provider details unavailable. A changed size or format can still prove that your fixture replaced a texture, without naming the mod.
- `preview` writes one diagnostic PNG of one texture. It requires the RGBA8 `Color` runtime format. It rejects sources wider or taller than 8192, sources above 16,777,216 pixels and invalid metadata. Other formats still work with `get`, but preview fails.
- A preview keeps the aspect ratio, never upscales and uses nearest-neighbor sampling. It is at most 512x512 and 2 MiB. The response holds the PNG path relative to `.sdvkit/lab/single/runtime`, the output size, the byte count and the SHA-256. The PNG stays below the ignored `.sdvkit` folder as review evidence. The game's cached texture is not changed or disposed.
- Unknown, colliding, non-texture, unclassified, oversized, stale, mismatched or unsafe requests return problems.

The [MCP preview](mcp.md#maps-and-textures) returns the checked PNG as image content with its metadata. There is no bulk preview, raw-pixel response, crop, source export, texture change or network-role variant.

## Inspect active audio metadata

In a `single` review, the `audio` subcommands list the audio IDs in the final `Data/AudioChanges` and `Data/JukeboxTracks` assets. They can also probe one cue through Stardew's public soundbank API. Each request reads the current assets through SMAPI's content pipeline, so normal SMAPI caching applies. No audio file is read directly.

```powershell
& $sdvkit project review audio cues --offset 0 --limit 100 --topology single --json
& $sdvkit project review audio cue "maintheme" --topology single --json
```

`cues` pages through these IDs in a stable order:

- each current `AudioCueData.Id`,
- each jukebox track key,
- each effective jukebox alternative-unlock ID.

How the IDs are reported:

- The `Data/AudioChanges` dictionary key is only an edit key. It is not reported as a cue. If several entries declare the same `Id`, the later one wins, as in Stardew's soundbank.
- An `alternativeUnlock` reference only means that hearing the old ID can unlock a jukebox entry. It does not make the ID a playable alias.
- Alternative IDs match ignoring case, and a later track replaces an earlier mapping. If an alternative matches exactly one playable ID, it annotates that ID instead of adding a second cue. Several matches fail. An unmatched alternative keeps the later spelling.
- Coverage's `jukeboxAlternativeReferences` counts every raw source reference. Each returned alternative ID has one effective relation.
- Each ID is probed without playback. The result reports whether the soundbank has it, whether its definition is available and its variant counts. For a current `AudioChanges` entry it adds category, stream, loop and reverb.
- A missing category is reported as Stardew's effective `Default`. An empty or unsafe category fails. An unspecified file list reports `null` data variants; an empty file list reports `0`.

The public soundbank API can check one cue but cannot list the built-in XACT cue bank. Coverage therefore reports `builtInCueCount: null` and `builtInCueInventoryStatus: "unavailableByPublicApi"`. A built-in probe does not imply a complete inventory.

`dataDefined` describes the current `AudioChanges` entry. `sessionResident` describes the current soundbank. They can differ: Stardew keeps an applied audio override for the whole session after its Data entry is removed. A data-derived ID can show `sessionResident: false` with null definition fields.

Cue IDs are case-sensitive. Unknown, case-mismatched, ambiguous, malformed, oversized or unsafe IDs fail, as do dummy or disposed soundbanks and stale or unsafe responses. Results do not include edit keys, audio file paths, `CustomFields`, raw banks, PCM or wave data. SDVKit never plays, records, changes or exports audio. Pages default to 50 IDs and accept 1-100. The [single-review MCP adapters](mcp.md#audio-and-observed-mod-assets) keep the same selections, fields and no-play, no-export rule.

## Inspect observed mod-owned asset namespaces

The `mod-assets` subcommands list `Mods/<owner>/...` asset requests seen since GameSide subscribed in a load-confirmed `single` review. This is a record of what loaded mods requested. It is not a file scan, and it misses assets that no mod requested in that time.

```powershell
& $sdvkit project review mod-assets assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review mod-assets keys "Mods/Example.Mod/Words" --offset 0 --limit 50 --topology single --json
& $sdvkit project review mod-assets get "Mods/Example.Mod/Words" "Greeting" --topology single --json
```

- `assets` reports, per asset:
  - the observed runtime type and the supported shape,
  - the owning mod, when the namespace matches one loaded mod ID,
  - request and ready counts,
  - the lifecycle generation and its state: requested, ready, invalidated or unavailable.
- Case and slash direction differences are merged. Hyphen/underscore name collisions and assets requested with several runtime types stay visible, and exact reads of them fail.
- Coverage is complete only when no request was dropped for a malformed name or the 2048-entry limit.
- Provider attribution is unavailable, because SMAPI's public API does not reliably expose which mod loaded or edited a final asset.
- `keys` and `get` read one already-observed asset. They support six shapes: string-to-string, string-to-integer, integer-to-string and integer-to-integer dictionaries, ordered string lists, and one string singleton. Unsupported entries appear in `assets` with `shape: null`, and key/value reads reject them.
- Dictionary keys are sorted ordinally. List keys are zero-based indexes. The singleton key is `singleton`.
- Pages default to 50 and accept 1-100. `get` takes no paging and returns one string or 32-bit integer.
- Asset operands are `Mods/<owner>/...` paths. Asset case and separators are tolerated when unambiguous. String keys match exactly and are capped at 480 UTF-16 code units. Asset operands and string keys must be well-formed text.
- Unknown, removed, unsafe, colliding, type-changing, unsupported, stale or mismatched requests return problems.

The [single-review MCP adapters](mcp.md#audio-and-observed-mod-assets) use the same response checks and coverage rules. There is no reflection, unknown-type serialization, bulk export, change, normal-`Mods` scan or network-role variant.

## Reference details

- Every request reuses the review's existing lock, process, staging, target-load, console-input and cleanup checks.
- Data: `--role` and `--screen` reads check the client binding. The SMAPI Data pipeline is shared by the whole process; SDVKit does not fake a per-player cache. Asset tokens, key tokens, one record and the whole response each have a size limit. There is no new public RPC and no generic reflection.
- Maps: the `Content/Maps` scan does not follow reparse points. Unsafe file names appear only as `invalid-map-asset-NNNN` labels and are not echoed.
- Textures: the scan limits both traversed entries and candidates. It does not follow reparse points. It drops localized copies by SMAPI's parsed locale, not a filename pattern. Classification reads at most the first 32 KiB output frame of each XNB through the loaded MonoGame decoder, within a 64 MiB total input budget. It does not create or cache the asset.
- Textures and mod assets: each response file and preview file is created new below the ignored review runtime. It is checked as a regular file without reparse points, bound to its request, size-limited and not reused. SDVKit does not clean up temporary files it did not create.

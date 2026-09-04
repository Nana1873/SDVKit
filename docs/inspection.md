# Inspect active game content

Use this reference to query the final SMAPI content pipeline of an already-running, load-confirmed **single** review. Start with the [live guide](live-review.md#start-a-review). Examples use the executable variable from [installation](../README.md#install).

Choose [Data](#read-canonical-data-definitions), [maps](#inspect-active-map-structure), [textures](#inspect-canonical-textures-safely), [audio](#inspect-active-audio-metadata), or [observed mod assets](#inspect-observed-mod-owned-asset-namespaces). Only Data is also available through [MCP](mcp.md#canonical-data).

## Shared rules

Require exit `0`, the requested canonical identity, and a current matching review result. Follow pagination instead of assuming the first page is complete. A failed or partial inventory is not complete coverage. Save diagnostic previews only below the role's isolated profile.

For operands beginning with `-` or matching option names, put all options before `--` and all operands after it. Unknown identities, unsupported shapes, ambiguous normalization, stale responses, or size limits are controlled problems; do not guess replacements or retry unchanged timeouts.

## Read canonical Data definitions

While an exact `single` review is running and its target identity is load-confirmed, the `data` subcommands provide bounded read-only access to every canonical structured `Data/*` definition asset shipped by the installed Stardew version. The game-side reader discovers the installed `Content/Data` asset names independently instead of relying on a maintained allowlist, then loads each asset through SMAPI's live game-content pipeline. The returned values therefore include the content packs and edits active in that exact review process; localized physical siblings aren't treated as separate canonical identities.

```powershell
& $sdvkit project review data assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review data keys "Data/Buildings" --offset 0 --limit 50 --topology single --json
& $sdvkit project review data get "Data/Buildings" "Barn" --topology single --json
```

`assets` reports the running game and file versions, each canonical asset name, loaded .NET data type, shape, and key kind. Its coverage object is complete only when every discovered asset is classified and safely queryable, with zero `unknown`, `unclassified`, and `unsupported` entries. Dictionaries use their canonical string or integer keys, lists use canonical zero-based indexes, and a singleton has the one explicit key `singleton`. `keys` returns those identities in deterministic order and never substitutes localized display values. `get` returns exactly one record with its canonical asset and key; object members are sorted deterministically in the JSON while source array order is retained.

Asset and string-key lookup accepts case differences plus small space, hyphen, underscore, slash, and backslash separator differences. Normalization collisions are ambiguous and fail closed; an exact canonical key remains selectable. A missing canonical `Data/*` name is reported as unavailable in the running game version, separately from a name outside that namespace. Load failures, unsupported key types, unsafe or oversized records, reparse points, stale responses, mismatched requests, and unknown or colliding identities are returned as bounded problems rather than guessed results.

Pages default to 50 entries and accept 1-100; offsets are non-negative. Asset and key tokens, one record, and the complete response are size-bounded. There is no unbounded full-dump default, no mutation operation, no network-topology variant, and no new public RPC or generic reflection surface. Each request reuses the existing exact review lock, process, staging, target-load, console-input, and cleanup checks.

## Inspect active map structure

The `map` subcommands inspect the canonical map assets visible through SMAPI's active content pipeline in the same exact, load-confirmed `single` review. They do not read through the normal `Mods` directory, export source XNBs, return a tile matrix, or mutate a map.

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

Run `& $sdvkit project review map --help` for the exact layer, direct-tile, and tile-index property forms. Supply only property names and coordinates known to exist in the selected map; SDVKit deliberately does not guess them, and an absent selection returns a machine-checkable `blocked` result. If a map, layer, or property operand starts with `-` or has the same spelling as an option, put every CLI option before the `--` end-of-options marker. Every following token is then treated as an operand.

`assets` independently scans the installed `Content/Maps` XNB candidates without following reparse points, excludes locale siblings, and classifies each pipeline result as a supported xTile map, a known non-map candidate, or an explicit gap. Unsafe physical candidate names are represented only by deterministic `invalid-map-asset-NNNN` labels and are never echoed into the report. This inventory describes physical game candidates only; an exact canonical `Maps/*` name introduced by a loaded mod can still be inspected through SMAPI's active content pipeline. Require `coverage.complete=true` before claiming complete physical-inventory support. A canonical name unavailable through that pipeline is reported separately from a name outside the namespace; load failures, normalized identity collisions, malformed warps, unsafe shapes, and oversized structures fail closed.

List operations are paged from stable collection order, while `get`, `layer`, `tile`, and `property` return one bounded selection. Warp entries distinguish general `Warp` routes (`playerAndNpc`) from NPC-only `NPCWarp` routes (`npc`). Tile output identifies an empty, static, or animated tile and returns stable frame references and property counts, never the layer's tile matrix. Exact property reads preserve their JSON type and require an explicit map, layer, direct-tile, or tile-index scope. An animated tile-index property additionally requires its stable zero-based `--frame`; direct and tile-index properties are never merged. Map access is intentionally CLI-only here and is not added to MCP.

## Inspect canonical textures safely

During that same exact, target-load-confirmed `single` review, the `texture` subcommands measure the non-localized `Content/**/*.xnb` population. `assets` classifies the physical XNB root TypeReader without loading its object graph: an exact built-in `Texture2DReader` is a texture, a narrow set of built-in Stardew data, collection, map, font, and effect readers are known non-textures, and every malformed, custom, or unknown reader is a gap. It pages only the identities classified as textures while its coverage object records all candidates, textures, non-textures, and classification gaps.

```powershell
& $sdvkit project review texture assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review texture get "LooseSprites/Cursors" --topology single --json
& $sdvkit project review texture preview "LooseSprites/Cursors" --topology single --json
```

The physical inventory bounds total traversed entries as well as candidate count, never follows reparse points, and uses SMAPI's parsed locale identity to exclude localized siblings without guessing from a filename regex. Classification reads at most the first 32 KiB output frame of each XNB through the already-loaded MonoGame decoder, with a 64 MiB aggregate input budget; it never instantiates or caches the asset. If an exact asset operand starts with `-` or matches an option name, put every CLI option before the `--` end-of-options marker; every following token is then an operand.

`get` loads only the selected canonical texture through the active content pipeline and returns its dimensions, runtime format, mip level count, running game versions, and availability. Exact reads accept an unambiguous case/separator-normalized token, return the canonical physical identity, and fail closed if multiple candidates collide after normalization. The supported public SMAPI API does not expose a reliable per-mod loader/editor chain for an arbitrary final texture, so the typed provenance object reports `final-post-pipeline` and explicitly marks detailed provider provenance unavailable. A changed final dimension or format can still prove a deliberately replaced live fixture without inventing which mod performed the edit.

`preview` reads back only that one selected texture after requiring the RGBA8 `Color` runtime format and rejecting source dimensions above 8192, source populations above 16,777,216 pixels, or invalid metadata. Unsupported compressed or differently packed formats remain metadata-readable through `get` but fail closed for preview instead of interpreting their bytes as RGBA. A supported preview preserves aspect ratio, never upscales, uses nearest-neighbor sampling, and writes at most one 512x512 diagnostic PNG with a 2 MiB encoded limit. The response contains only its GUID-derived path relative to `.sdvkit/lab/single/runtime`, output dimensions, byte count, and SHA-256; the PNG itself remains below ignored `.sdvkit` as review evidence. The cached game texture is not mutated or disposed.

Every response and preview target is create-new, regular-file and reparse checked, request-bound, size bounded, and never reused. Unknown, colliding, non-texture, unclassified, oversized, stale, mismatched, or unsafe requests fail closed. There is no bulk preview, raw-pixel/base64 response, crop API, source-XNB export, texture mutation, network-role variant, or texture MCP tool.

## Inspect active audio metadata

An exact active `single` review can inventory the bounded audio identities visible through the final `Data/AudioChanges` and `Data/JukeboxTracks` assets, or probe one exact cue through Stardew's public soundbank API. Every request reads the current final asset state through SMAPI's active content pipeline, so normal SMAPI cache and invalidation behavior still applies and no audio file is read directly.

```powershell
& $sdvkit project review audio cues --offset 0 --limit 100 --topology single --json
& $sdvkit project review audio cue "maintheme" --topology single --json
```

`cues` returns stable ordinal pages from the union of current `AudioCueData.Id` values, jukebox track keys, and effective jukebox alternative-unlock IDs. The `Data/AudioChanges` dictionary key is only the modification key and is never reported as a playable cue; if multiple entries declare the same exact `Id`, the later final-pipeline entry wins just as Stardew's soundbank update does. Source categories and jukebox relationships remain distinct: an `alternativeUnlock` reference means only that hearing the old ID can unlock a jukebox entry, never that the ID is a playable soundbank alias. Alternative IDs are matched globally with ordinal-ignore-case semantics, and a later track replaces an earlier mapping. An alternative which matches exactly one playable data or primary-track identity case-insensitively annotates that canonical identity instead of inventing a second cue; multiple playable matches fail closed, while an unmatched alternative keeps the later effective spelling. Coverage's `jukeboxAlternativeReferences` still counts every bounded raw source reference, while each returned alternative identity has only its one effective relation. Each returned identity is probed without playback and reports only current soundbank existence, definition availability and variant counts, plus the bounded category, stream, loop, and reverb fields for a current `AudioChanges` entry. A null/omitted category is reported as Stardew's effective `Default`, while an explicitly empty or otherwise unsafe category fails closed; an unspecified file list and an explicitly empty file list remain distinct as `null` and `0` data-variant counts.

The public soundbank API can check an exact cue but cannot enumerate the built-in XACT cue bank. Coverage therefore reports `builtInCueCount: null` and `builtInCueInventoryStatus: "unavailableByPublicApi"`; an exact built-in probe does not expand or imply a complete built-in inventory. `dataDefined` describes the current post-pipeline `AudioChanges` entry, while `sessionResident` describes the current soundbank. Those values intentionally remain independent because Stardew keeps an applied audio override resident for the game session after its Data entry is removed.

Cue IDs are case-sensitive. If a cue operand starts with `-` or matches an option name, put every CLI option before the `--` end-of-options marker. Unknown, case-mismatched, non-exact ambiguous, malformed, oversized, unsafe-ID, dummy-bank, disposed-bank, stale-response, and unsafe-response cases fail closed. Results never expose modification keys, audio file paths, `CustomFields`, raw banks, PCM or wave data; they never play, record, mutate, or bulk-export audio. Pages default to 50 identities, accept limits of 1-100, and reuse the exact owned-review transport and cleanup boundary. Native MCP exposure is intentionally not part of this capability.

## Inspect observed mod-owned asset namespaces

The `mod-assets` subcommands expose a bounded read-only catalogue of conventional `Mods/<owner>/...` asset requests observed after AlwaysOn subscribed in the same exact, target-load-confirmed `single` review. This is lifecycle evidence, not a filesystem scan or a complete inventory of assets which no loaded mod requested during that interval.

```powershell
& $sdvkit project review mod-assets assets --offset 0 --limit 100 --topology single --json
& $sdvkit project review mod-assets keys "Mods/Example.Mod/Words" --offset 0 --limit 50 --topology single --json
& $sdvkit project review mod-assets get "Mods/Example.Mod/Words" "Greeting" --topology single --json
```

`assets` reports the observed runtime type, resolved namespace-owner identity when it matches one loaded mod ID, supported adapter shape, request and ready counts, lifecycle generation, and whether the current generation is requested, ready, invalidated, or unavailable. SMAPI identity casing and slash direction are treated as equivalent and consolidated, while stable hyphen/underscore name collisions and multiple requested runtime types stay visible and fail closed for exact reads. Coverage is complete only when no conventional observed request was dropped by a malformed identity or the 2048-entry catalogue bound. Detailed loader/editor provider attribution remains explicitly unavailable because the supported public SMAPI API does not reliably expose it for an arbitrary final asset.

`keys` and `get` load only one already-observed exact asset through SMAPI's active content pipeline and only through six reviewed adapters: string-to-string, string-to-integer, integer-to-string, and integer-to-integer dictionaries, ordered string lists, and one string singleton. Dictionary keys are ordinal-sorted, list keys are zero-based indexes, and the singleton key is `singleton`. Pages default to 50 with limits from 1 through 100; `get` accepts no pagination and returns only one primitive string or 32-bit integer. Asset operands stay canonical `Mods/<owner>/...` paths, keys are capped at 480 UTF-16 code units, and both require well-formed text. If an asset or key resembles an option, put every CLI option before `--`; all following tokens are operands.

Every response uses a request-bound create-new regular file below the ignored review runtime, with a bounded exact JSON shape and no reused or foreign temporary-file cleanup. Unknown, removed, unsafe, colliding, type-changing, unsupported, stale, or mismatched requests fail closed. There is no arbitrary reflection, unknown-type serialization, bulk export, mutation, normal-`Mods` scan, network-role variant, or mod-asset MCP tool.

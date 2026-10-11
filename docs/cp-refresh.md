# Refresh selected Content Patcher patches

Refresh reloads edited CP patch JSON in a running review, without a restart.

## Before you start

Start a single [review](live-review.md#start-a-review) with one root CP pack and a ready-folder Content Patcher **2.9.1** [companion](glossary.md#reviews-and-ownership). Keep the SMAPI console idle.

Prepare the pack and provider config before launch. CP can rewrite valid config files to normalize formatting and add defaults. If that happens on the first launch, refresh rejects the changed staging. To fix it:

1. Copy the generated staged config files to an ignored `.sdvkit/` working folder.
2. Stop and reset the review. A single review's stop removes staging.
3. Adopt the saved config bytes in your sources and start again.

Refresh itself never copies config back to your source.

## Run a refresh

Edit your source patch JSON, then run from the same lab folder:

```powershell
& $sdvkit project review cp-refresh .\ExamplePack --pack Example.Pack --provider Pathoschild.ContentPatcher --file content.json --file patches/items.json --observe-data Data/Objects --key 388 --json
& $sdvkit project review status --json
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --asset Data/Objects --json
```

Select only the existing files you edited. Paths are relative to the source pack recorded at review start. `content.json` is optional when only an Include file changed. Include files must be reachable from `content.json` through literal `Include`/`FromFile` paths, including comma-separated and nested includes. Their documented shape is `{ "Changes": [...] }`. SDVKit checks selected Include patches against the bundled official CP schema. This does not make `project check` validate Includes recursively.

The command:

1. prepares candidate and original files under the ignored `.sdvkit/`;
2. replaces only the selected staged files;
3. sends `patch reload "Example.Pack"` and captures CP's reply;
4. runs a [CP diagnosis](cp-diagnosis.md);
5. reads the Data record you chose with `--observe-data` and `--key`.

The source pack, provider and companions are only read. No other CLI or MCP action, stop or reset can run in between.

## Read the result

`state=observed` with exit `0` means: CP acknowledged the reload, the diagnosis matched, and the selected record was read in the same game process. **Compare the returned record with your expected value.** The result does not prove that your patch caused the value, that every patch applied or that a UI showed it. Diagnosis runs before the Data read, because the read can load an asset and change its later `applied` state.

`launchId`, `process` and `elapsedSeconds` describe this operation. To record same-process proof, compare the process ID and start time with the review before the refresh. To compare with a full restart, time the same example and the same observation.

## Supported changes and bounds

CP 2.9.1's [reload contract](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/troubleshooting.md#reload) reloads patches, including [Include files](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/action-include.md). It does not reload other definitions such as ConfigSchema, DynamicTokens and CustomLocations.

What must not change (otherwise stop, reset, rebuild if needed and start again):

- the root file's fields other than `Changes`, by meaning;
- byte for byte: manifest, config, i18n, assets, DLLs, code, providers and every unselected file;
- the file list: no added or deleted files.

Limits:

- Up to 16 unique existing `.json` paths. Each original and candidate is at most 1 MiB, together at most 4 MiB.
- Paths use `/`, ASCII letters and digits, spaces, `.`, `_` and `-`. No rooted paths, traversal, alternate streams, symbolic links, junctions or hard links.
- Literal Include graph: at most 64 files of at most 1 MiB each. Cycles and tokenized Include paths need a restart. Token validity and whether a patch applies still need diagnosis and observation.
- Each pack, provider and companion source and staged tree: at most 4,096 entries and 256 MiB. Companions must be unchanged ready folders. A companion built from a C# project needs a restart.
- Diagnosis limits apply: the review log, CP 2.9.1 output only and no manual console commands. Unknown output or extra provider errors return incomplete, even if CP also printed its success line.

Packaging before or during the review does not change the selection. For the selected CP target, the root `.sdvkit` output is left out of copying and comparison. It is never copied into the game or deleted. Links and hard links stay rejected, also inside that output, and size limits cover the whole source tree. Nested `.sdvkit` folders, other development, save or game files, unselected edits and provider or companion changes are rejected.

MCP offers the same operation as [`stardew_cp_refresh`](mcp.md#content-patcher-diagnosis-and-opt-in-refresh), with its own opt-in `--allow-cp-refresh`. It is bound to the launch and root source at server start. Input and fixture opt-ins never grant it. There is no file watcher, general file sync, asset or code hot reload, and no `network-2` refresh.

## Identity and interrupted operations

The review's `buildIdentity` and the GameSide target identity keep the hash of the files at **launch**. They still bind the same process. They do not prove that the reloaded JSON is active in memory.

Review status shows a `cpRefresh` entry on the target with a separate `stagedBuildIdentity`. It also lists the previous staged hash, the selected files, the refresh and launch IDs, command delivery and `requiresRestart`. Every later operation checks the current staged hash. CP diagnosis reports that hash. A log read that spans a changed hash or refresh fails closed.

The log covers the whole launch, so warnings and errors can be older than the current hash. The reported hash names the current staged bytes, also when the reload is uncertain. It does not tie old entries to that version or prove the reload worked. Check the status receipt for pending recovery.

If a copy fails before reload, SDVKit tries to restore the selected originals. `filesReplaced` counts confirmed replacements. `stagingRestored` says whether the full original staged hash came back. An incomplete rollback stays blocked by the normal identity checks.

If reload delivery, its reply, the diagnosis, the observation or the final status update is uncertain, the staged files stay in place and the receipt requires a restart. [`commandWritten`](glossary.md#commands-and-results) `true` means the reload ran; `null` means it may have run. SDVKit never retries a reload and never rolls back files after a reload may have run. A second refresh is rejected while recovery is pending.

To recover, read status and diagnosis if you can, keep the incomplete result, then restart:

```powershell
& $sdvkit project review stop --json
& $sdvkit project review reset --topology single --json
& $sdvkit project review start .\ExamplePack --topology single --companion .\ContentPatcher --test-save --json
```

Keep your original `--test-save` choice. Do not delete markers, kill processes by name or replay an uncertain reload. Finish successful reviews with the same [stop and reset](live-review.md#finish-or-test-persistence).

## Reference details

- The lab operation lock and the review action lock cover the whole refresh.
- Selected Include patches are validated with a temporary `Format` wrapper.
- Staged runtime identities include every file, also any unexpected `.sdvkit` payload.
- Before the first replacement, SDVKit records the intended staged hash and `requiresRestart=true` atomically in the ownership marker.
- Originals and candidates stay under `.sdvkit/lab/single/review-prepared/` as local evidence. They are not an alternative review state.
- Reload accepts CP 2.9.1's one known cache-invalidation trace before the full INFO acknowledgment, including SMAPI's propagation log in between.
- Stop and reset accept changed staged content only for cleanup after the process has exited. They still verify the paths and links the marker names.

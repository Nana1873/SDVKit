# Refresh selected Content Patcher patches

Start an [owned single review](live-review.md#start-a-review) with one root CP pack
and an explicit ready-directory Content Patcher **2.9.1** companion. Prepare pack
and provider config before launch. Keep the SMAPI console idle. Edit your source
patch JSON, then run from the same lab directory:

```powershell
& $sdvkit project review cp-refresh .\ExamplePack --pack Example.Pack --provider Pathoschild.ContentPatcher --file content.json --file patches/items.json --observe-data Data/Objects --key 388 --json
& $sdvkit project review status --json
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --asset Data/Objects --json
```

Select only the existing files you edited; paths are relative to the exact source
pack recorded at review start. `content.json` is optional when only an Include
file changed. Include files must be reachable from `content.json` through literal
`Include`/`FromFile` paths, including comma-separated and nested includes. Their
documented shape is `{ "Changes": [...] }`. Selected Include patches are checked
against the bundled official CP schema with a temporary Format wrapper; this
does not extend `project check` to recursive Include validation.

The command prepares candidates and originals under ignored `.sdvkit/`, replaces
only the selected staged files, sends `patch reload "Example.Pack"`, captures
the provider's reload reply and [CP diagnosis](cp-diagnosis.md), then executes the
existing Data `get` observation explicitly selected by `--observe-data`/`--key`.
The source pack, provider and companions are read only. Both existing lab operation
and review action locks cover the entire operation, so concurrent CLI/MCP actions
and stop/reset cannot interleave with its copies or observations.

`state=observed` and exit `0` mean reload was acknowledged, diagnosis was correlated,
and the selected record was read in the same exact owned process. **Compare the
returned record with your expected value.** This does not assert that your patch
caused it, that every patch applied, or that a UI rendered it. Diagnosis precedes
Data inspection, which can load an asset and change its later `applied` state.
`launchId`, `process` and `elapsedSeconds` describe this operation; compare both
process ID and start time with the pre-refresh review when recording same-process
proof. Times for a full restart must use the same example and observation endpoint.

## Supported changes and bounds

The installed version's [reload contract](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/troubleshooting.md#reload)
reloads patches, including [Include files](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/action-include.md).
Non-patch definitions such as ConfigSchema, DynamicTokens and CustomLocations are
not reloaded. The root's fields other than `Changes` must remain semantically
identical. Manifest, config, i18n, assets, DLLs, code, file additions/deletions,
providers and every unselected file must remain byte-identical to staging.
Otherwise stop/reset, rebuild when needed, and start again.

- Up to 16 unique existing `.json` paths; each original and candidate at most
  1 MiB; their combined size at most 4 MiB.
- Paths use `/`, ASCII letters/digits, spaces, `.`, `_`, `-`; no rooted paths,
  traversal, alternate streams, symbolic links, junctions or hard links.
- Literal Include graph: at most 64 files, each at most 1 MiB; cycles and
  tokenized Include paths require restart. Runtime token validity and patch
  applicability still require diagnosis and observation.
- Each selected pack/provider/companion source and staged tree: at most 4,096
  entries and 256 MiB. Companions must be unchanged ready directories; a companion
  built from a C# project requires restart for this refresh slice.
- Existing diagnosis limits still apply: bounded owned log, exact CP 2.9.1 output,
  and no concurrent manual console typing. Unknown output or additional provider
  errors return incomplete, even if CP also printed its success line.

There is no watcher, arbitrary filesystem synchronization, asset/code hot reload,
network-2 refresh or native MCP mutation command.

## Identity and interrupted operations

The review's `buildIdentity` and AlwaysOn/runtime target identity retain the
original **launch** file-set hash. They continue to bind the same process; they
do not prove that reloaded JSON is active in memory. The existing ownership marker
and review status's target `cpRefresh` contain a separate `stagedBuildIdentity`,
the previous staged hash, selected files, refresh/launch IDs, command delivery and
`requiresRestart`. Every subsequent owned operation checks the actual current
staged hash. CP/log diagnosis reports the current staged hash. A log read spanning
a changed hash or refresh generation fails closed.

Warning/error entries can be older than that hash: the log covers the entire
launch. Its reported staged hash identifies the currently owned bytes, including
when reload is uncertain; it does not attribute historical entries to that
generation or establish reload success. Check the status receipt for pending recovery.

Before the first replacement, SDVKit atomically records the intended staged hash
and `requiresRestart=true`. If a copy fails before reload, it attempts to restore
the selected originals. `filesReplaced` counts confirmed replacements;
`stagingRestored` explicitly reports whether the complete original staged hash
was recovered. An incomplete rollback remains blocked by normal identity checks.
Originals and candidates remain under `.sdvkit/lab/single/review-prepared/` as local
evidence; they are not an alternate authoritative review state.

If reload delivery, its reply, diagnosis, observation or the final marker update
is uncertain, the staged files remain visible and the durable receipt requires
restart. `commandWritten=true` means delivered; null can mean it ran. SDVKit never
retries reload automatically and never rolls back files after possibly executed
reload. A second refresh is rejected while recovery is pending.

Inspect status and diagnosis when available, retain the incomplete result, then
use the existing exact recovery path:

```powershell
& $sdvkit project review stop --json
& $sdvkit project review reset --topology single --json
& $sdvkit project review start .\ExamplePack --topology single --companion .\ContentPatcher --test-save --json
```

Retain your original `--test-save` choice. Stop/reset deliberately allow staged
content drift only for cleanup after exact process exit, while continuing to
verify marker-selected paths and links. Do not delete markers, kill by process
name, or replay an uncertain reload. Finish successful reviews with the same
[required stop/reset](live-review.md#finish-or-test-persistence).

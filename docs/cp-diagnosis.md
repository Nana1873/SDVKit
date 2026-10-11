# Diagnose one Content Patcher change

Start a single [owned](glossary.md#reviews-and-ownership) [review](live-review.md#start-a-review) with your pack and a local Content Patcher 2.9.1 [companion](glossary.md#reviews-and-ownership). Diagnosis reads the review's SMAPI log, so do not type commands into the SMAPI console while it runs. SDVKit never retries a diagnosis command automatically. Run from the same lab folder:

```powershell
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --json
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --asset Data/Objects --parse '{{Season}}' --json
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --asset Data/Objects --order --json
# Only after retaining the diagnosis, observe the actual final asset:
& $sdvkit project review data get Data/Objects 388 --json
```

`state=ready` means SDVKit matched and recognized CP's reply. It does not mean the patch worked. Read `summary.patches` and the CP `messages` it keeps:

| Observation | Meaning |
| --- | --- |
| `packLoaded`, `providerLoaded` | The staged IDs and versions appear in a fresh loaded-mod report for this review. |
| `loadedAndEnabled` | CP loaded and enabled this patch. False: read CP's reason, then run the offline `project check` or `review diagnostics` when relevant. |
| `conditionsMatch` | CP says the current conditions match. False: read the named condition and the local token context. |
| `applied` | CP says the target was loaded and patched. False while the two previous columns are true can mean the asset has not loaded yet. It does not prove a wrong target. |
| `commandWritten` | The diagnosis command ran on the review's game thread. A timeout or missing match stays incomplete. |
| Separate Data/map/texture result | The value seen through the running content pipeline at that later time. It does not say which mod set it. |

### Example: a deliberate failure

1. Write a patch to `Data/Objects` with a `When` condition that does not match the test save's season.
2. Diagnose without `--asset` first, so patches with invalid or unresolved targets stay visible.
3. Use `--parse '{{YourConfigValue}}'` to check a token in **this pack's** context. Unclosed or unavailable tokens keep CP's own errors.
4. Fix your source. Stop and reset the review, then start it again with the same provider.
5. Run a new diagnosis. Then read the affected record and compare the expected field.
6. Finish with the [stop and reset](live-review.md#finish-or-test-persistence).

For supported patch-only edits, [refresh selected JSON](cp-refresh.md) can replace the restart and keep the same running process.

## Explain competing patches on one asset

Add `--order` with one canonical `--asset`. SDVKit then keeps two extra observations, after `summary` and the optional `parse`, each with its own timestamps:

- `applied.messages`: patches in CP's expected apply order, with the `[X]`/`[ ]` applied checkboxes and action names. Read this to understand the apply order.
- `order.messages`: the same active patch paths in global definition order, with their global position and hierarchical index path.

`orderAsset` names the selected asset. `patches` stays the summary's boolean table; it is empty in the dump response objects.

The global definition order is not the apply order: CP applies `Load` patches before other actions within a target. Results include competing CP patches from other packs for this asset and leave out other assets. An empty selected group is valid and returns headings with no rows. Inactive patches without a current target are not in the order; diagnose their conditions with the normal unfiltered summary.

Both reports describe CP patches only. They do not cover C# content edits, per-field provenance or a guaranteed winning value, and they are not general conflict detection. Observe the final asset separately after you keep the diagnosis.

A partial order never returns `ready`. Problem codes:

- `cpOrderOutputTruncated`: a full dump reply exceeded the limits, even if the selected part is small.
- `cpOrderPrivateContextWithheld`: a selected row contained known private context, so the whole order is withheld.
- Incomplete: unknown output, duplicate or ambiguous patch paths, missing definition rows or a changed selection.

For a reproducible unexpected winner, follow the [two-patch recipe](cp-patch-order-recipe.md).

## Boundaries and interpretation

The workflow uses the documented [`patch summary` and `patch parse`](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/troubleshooting.md) commands. Other provider versions return `cpVersionUnsupported` before any command is sent.

Inputs:

- `--pack` must be a staged pack ID. CP flag words are rejected as pack IDs.
- `--asset` is a canonical base name: ASCII letters and digits, spaces, `_`, `-` and single `/` separators. Localized names such as `Data/furniture.fr-FR`, dotted names, empty segments and spaces around segments are rejected before dispatch.
- `--parse` is at most 512 characters. Quotes, backslashes, semicolons and control characters are rejected.
- SDVKit exposes no arbitrary command operand, no `patch export` and no invented validation command.

Timing: `startedAtUtc` and `completedAtUtc` bracket each observation. `logTime` is CP's log clock, without a date. Summary runs before the optional parse. SDVKit inspects no asset by itself, because **inspection can load an asset and change the next summary**.

Privacy: the [shared log rules](live-review.md#diagnose-selected-mod-warnings-and-exceptions) apply. Results keep at most 256 lines of 1,024 characters and report `truncated` and `withheldLines`. Global-token sections and other staged mods' context are left out. Known absolute paths and secret-bearing context are withheld; relative paths stay. Known sensitive local-token rows are withheld. Parsing a known sensitive token withholds the whole response as incomplete. This is not full sanitization of arbitrary text or indirect custom-token values. Logs stay local and available through the guide; nothing is uploaded.

MCP offers the same diagnosis as [`stardew_cp_diagnose`](mcp.md#content-patcher-diagnosis-and-opt-in-refresh). [Selected JSON refresh](cp-refresh.md) needs its own MCP startup opt-in; diagnosis never grants it.

## Reference details

- The order adapter follows the CP 2.9.1 [`DumpCommand`](https://github.com/Pathoschild/StardewMods/blob/b2d750f944dff2c0a540a02dd2af653b09e378c4/ContentPatcher/Framework/Commands/Commands/DumpCommand.cs), which writes `dump applied` and `dump order` at `INFO` level. CP 2.9.1's [summary](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/Framework/Commands/Commands/SummaryCommand.cs) and [parse](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/Framework/Commands/Commands/ParseCommand.cs) reply at `DEBUG` level.
- CP's dumps take no asset or pack operand. SDVKit runs `patch dump applied`, then `patch dump order`, and filters locally. Only the selected asset group and its definition rows leave the adapter.
- Both full dump replies must fit 256 lines of 1,024 characters within the shared 4 MiB log scan. The whole log must fit that scan and start with a complete line.
- Each filter is passed as one double-quoted console argument.
- Unique `patch parse` marker commands in the selected pack's context bracket each reply, sent through the review console. The review action lock runs one diagnosis at a time. The reader accepts exactly one provider reply between two matching markers. It checks launch, process, staging, freshness and that the same log file keeps growing.
- Extra provider entries, overlapping commands, missing markers, a replaced log, unknown output or a ten-second timeout give an incomplete result. Single-line entries from other loggers are skipped. Multiline output from another logger inside the window is rejected, because it can hide the rest of an interrupted CP reply. A parse reply must include its final result.
- This matches ordinary provider output; it does not authenticate the origin of arbitrary mod text.

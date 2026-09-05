# Diagnose one Content Patcher change

Start an [owned single review](live-review.md#start-a-review) with your pack and an
explicit local Content Patcher 2.9.1 companion. Use an idle SMAPI console without
concurrent manual commands. From the same lab directory:

```powershell
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --json
& $sdvkit project review cp-diagnose --pack Example.Pack --provider Pathoschild.ContentPatcher --asset Data/Objects --parse '{{Season}}' --json
# Only after retaining the diagnosis, observe the actual final asset:
& $sdvkit project review data get Data/Objects 388 --json
```

`state=ready` means the bounded CP response was correlated and recognized. It does
not mean the patch worked. Read `summary.patches` and the preserved CP `messages`:

| Observation | Meaning |
| --- | --- |
| `packLoaded`, `providerLoaded` | Exact staged IDs and versions appear in the fresh owned loaded-mod report. |
| `loadedAndEnabled` | CP loaded and enabled this patch. False: read CP's reason, then run the offline `project check` or selected `review diagnostics` when relevant. |
| `conditionsMatch` | CP says the current conditions match. False: read the named condition and local token context. |
| `applied` | CP says the target was loaded and patched. False with both previous columns true can mean an asset has not loaded yet; it is not proof of an incorrect target. |
| `commandWritten` | The diagnosis command reached the owned console. Timeout or missing correlation remains incomplete. |
| Separate Data/map/texture result | The value actually observed through the running content pipeline, at that later time. This is not a per-mod provenance claim. |

For a deliberate failure, author a patch to `Data/Objects` with a `When` condition
which does not match the disposable world's season. Diagnose without an asset
filter first, so patches with invalid/unresolved targets remain visible. Use
`--parse '{{YourConfigValue}}'` to examine a token in **this pack's** context;
unclosed or unavailable tokens retain CP's own errors. Correct your source,
stop/reset the exact review, and start it again with the same selected provider.
Capture a new diagnosis, then inspect the affected record and compare its expected
field. Finish with the [required stop/reset](live-review.md#finish-or-test-persistence).
For supported patch-only edits, [refresh selected JSON](cp-refresh.md) can replace
that restart while preserving the exact running process.

## Boundaries and interpretation

The workflow uses documented [`patch summary` and `patch parse`](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/troubleshooting.md).
CP 2.9.1's [summary](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/Framework/Commands/Commands/SummaryCommand.cs)
and [parse](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/Framework/Commands/Commands/ParseCommand.cs)
emit normal informational command replies at `DEBUG` level. Other provider
versions return `cpVersionUnsupported` before commands are sent.

Each response is bracketed by unique literal `patch parse` markers in the selected
pack context, using the existing review console. An existing review action lock
serializes diagnoses. The reader accepts exactly one provider reply between the
two matching markers, with launch/process/staging/freshness verification and
same-file append continuity. Extra provider entries, overlapping commands, missing
markers, log replacement, an unknown output shape, or a ten-second response-window
timeout return an explicit incomplete result. No command is automatically retried.
Single-line unrelated entries from other loggers are omitted. Foreign multiline
output inside the window is conservatively rejected: it can hide the remainder
of an interrupted CP reply. A parse reply must include its actual final result.
This is bounded correlation of
ordinary provider output, not authenticated provenance of arbitrary mod text.

Each filter is passed as one double-quoted console argument. Pack IDs must be selected staged IDs;
CP flag words are rejected as pack IDs. Asset input is a base name with ASCII
letters/digits, spaces, `_`, `-`, and single `/` separators. Localized names such as
`Data/furniture.fr-FR`, dotted names, empty segments and whitespace around segments
are rejected before dispatch; pass the canonical base name. Parse input is at
most 512 characters; quotes, backslashes, semicolons and control characters are
rejected. No arbitrary command operand, `patch export`, or invented validation
command is exposed.

`startedAtUtc`/`completedAtUtc` bound each observation; `logTime` is CP's log clock,
without an inferred date. Summary precedes optional parse. No asset is inspected
automatically: **inspection can load an asset and change the next summary**.

The [shared owned-log boundary](live-review.md#diagnose-selected-mod-warnings-and-exceptions)
applies. Diagnosis requires the complete log to fit its 4 MiB scan and a complete
starting line. Results retain at most 256 lines of 1,024 characters, with explicit
`truncated` and `withheldLines`. Global-token sections and unrelated staged-mod
context are omitted. Known absolute paths and secret-bearing context are withheld;
relative paths survive. Known sensitive local-token rows are withheld, and parsing
a known sensitive token withholds its entire response as incomplete. This is not
complete sanitization of arbitrary text or indirect custom-token values. Local
owned logs remain available through the existing guide; no log is uploaded.

The CLI is the first surface. The shared read-only diagnostic MCP tool remains
available for warnings/errors; a dedicated CP MCP command is deferred.
[Selected JSON refresh](cp-refresh.md) is CLI-only. This workflow does not establish general conflict detection.

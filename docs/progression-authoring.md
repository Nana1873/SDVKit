# Observe one NPC, mail and quest workflow

Use the original [ProgressionProbe source](examples/progression-recipe/ProgressionProbe/ModEntry.cs)
to diagnose one wrong friendship threshold, correct its configuration, observe its
mail and quest effects, and prove persistence after two actual process restarts.
This recipe uses the installed Leah NPC and original rain-barrel mail/quest text;
it introduces no NPC enumeration or general progression editor.

**Acceptance status:** offline builds and contract tests do not establish live
behavior. Retain exact-package live evidence for [#235](https://github.com/Nana1873/SDVKit/issues/235)
before claiming this recipe passed. Record the actual game/SMAPI versions, package
and DLL hashes, owner, fixture/save IDs and each process identity in that evidence.

## Read-only contract

```powershell
& $sdvkit project review progression --npc Leah `
    --mail SDVKit.ProgressionProbe_RainBarrel `
    --quest SDVKit.ProgressionProbe_CheckBarrel --json
```

Native MCP exposes `stardew_progression_get` with exactly `npcId`, `mailId` and
`questId`. All three selections are required, case-sensitive, and 1–128 ASCII
letters, digits, dots, underscores or hyphens. There is no list-all operation.
CLI and MCP delegate to the same service. Neither tool mutates progression.

The first supported context is an unbound `single` review of the exact owned,
ready disposable test save. Network roles, local-screen bindings, ordinary
non-fixture reviews, missing local-player facts and non-ready worlds fail
explicitly. Responses are bounded to 32 KiB, matched to the unique request,
launch, role, fixture, farmer and selected IDs, and checked for freshness again
after the final ownership read. No peer or old-capture fallback is allowed.

`data` contains only the selected facts, `playerId`, `fixtureId` and capture tick:

| Fact | Meaning |
| --- | --- |
| `npc.state=missing` | `Game1.getCharacterFromName(npcId)` returned no NPC; points are null. |
| `npc.state=noFriendship` | The NPC exists but the local farmer has no selected `friendshipData` entry; points are null. |
| `npc.state=available` | Raw signed `Friendship.Points`; no invented vanilla clamp or inferred heart level. |
| `mail.received` | Exact membership in this farmer's `mailReceived`, which may also contain non-letter flags. |
| `mail.tomorrow` | Exact membership in `mailForTomorrow`; suffix variants such as `%&NL&%` are different IDs. |
| `mail.mailboxCount` | Number of exact selected-ID entries in `mailbox`, bounded to 100. |
| `quest.matchCount`, `acceptedCount`, `completedCount` | Counts of current `questLog` entries with the exact selected ID and corresponding native flags, bounded to 100. |

All-false mail or zero quest matches means no selected state in those collections;
it does not prove the asset definition is missing. Completed quests removed from
the log cannot be reconstructed, and no historical completion is inferred. A
removed quest or out-of-range capture fails or becomes absent explicitly; it is
never replaced with a previous result. This contract does not inspect dialogue,
schedules, special orders, relationship status, rewards or arbitrary mod data.

## Prepare and package the original mod

Follow [lab preparation](live-review.md#prepare-the-lab) with one explicit
installation and exclusive ownership. Keep normal Saves and Mods outside the
workflow. Copy only `ProgressionProbe.csproj`, `ModEntry.cs`, `manifest.json` and
`config.json` from [the source directory](examples/progression-recipe/ProgressionProbe)
to a fresh directory below the lab's ignored `.sdvkit/`; do not run the docs tree.
The default config deliberately requires 750 points instead of the intended 500.
No companion/provider mod is required.

```powershell
$source = Join-Path $PWD '.sdvkit\progression\source\ProgressionProbe'
$evidence = Join-Path $PWD '.sdvkit\progression\evidence'
New-Item -ItemType Directory -Force $evidence | Out-Null
& $sdvkit project check $source --json |
    Tee-Object (Join-Path $evidence 'check.json')
& $sdvkit project build $source --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'build.json')
& $sdvkit project package $source --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'package.json')
```

Require passing reports. Extract the returned exact mod ZIP to a fresh ignored
directory and select the directory containing `manifest.json` and
`ProgressionProbe.dll` as `$ready`. Retain ZIP, DLL and original config hashes.
Every following live command runs from the same lab root using the same SDVKit
distribution; record its package hash separately from the mod ZIP hash.

```powershell
& $sdvkit project review start $ready --game-path $gamePath --topology single --test-save --json
& $sdvkit project review status --json
& $sdvkit project review diagnostics --mod SDVKit.ProgressionProbe --json
```

Require the exact target loaded with no mod error and the owned fixture ready.
Use a native MCP SDK client against `project review mcp serve --topology single`
and retain initialization, `tools/list`, and the selected progression calls.
Default discovery must include `stardew_progression_get`; network and screen
discovery must exclude it. Mutation permissions are not needed for these reads.

## Diagnose and correct the wrong condition

```powershell
& $sdvkit project review command 'progression_probe_prepare' --json
& $sdvkit project review progression --npc Leah --mail SDVKit.ProgressionProbe_RainBarrel --quest SDVKit.ProgressionProbe_CheckBarrel --json
```

Preparation is **synthetic**: the helper seeds exactly 500 Leah friendship points
in the disposable save. It refuses pre-existing recipe effects rather than
clearing them. Require `npc.state=available`, points 500, `received=false`,
`tomorrow=false`, mailbox count 0 and quest match count 0. The selected mod log
must say `PROGRESSION_PROBE_BLOCKED ... required=750`. Read the same IDs through
native MCP. Delivery of a helper command alone proves neither failure nor effect.

```powershell
& $sdvkit project review command 'progression_probe_correct' --json
& $sdvkit project review config-reconcile --mod SDVKit.ProgressionProbe --json
& $sdvkit project review command 'progression_probe_evaluate' --json
& $sdvkit project review progression --npc Leah --mail SDVKit.ProgressionProbe_RainBarrel --quest SDVKit.ProgressionProbe_CheckBarrel --json
```

The small developer correction changes only this recipe's required points from
750 to 500 and saves its already-packaged root config. Require `state=reconciled`,
the previous/accepted config hashes, unchanged non-config identity and a verified
export. Do not edit ownership markers. Require the same launch/DLL and fresh
observations with exactly one mailbox entry and exactly one incomplete quest;
receipt remains false. This proves the original mod's delivered effects, not a
player speaking to Leah. Repeat evaluation and require counts stay exactly one.

## Native receipt and completion

Use the existing disposable fixture navigation to enter Farm, with no active menu:

```powershell
& $sdvkit project review command 'sdvkit fixture farm' --json
& $sdvkit project review command 'progression_probe_open_mail' --json
& $sdvkit project review progression --npc Leah --mail SDVKit.ProgressionProbe_RainBarrel --quest SDVKit.ProgressionProbe_CheckBarrel --json
& $sdvkit project review menu --json
```

The recipe places only its selected letter first without removing other letters.
The helper invokes `Farm.checkAction` at the installed public mailbox position;
the native game mailbox handles delivery and opens the letter. Require a fresh
capture with `received=true`, mailbox count 0 and tomorrow false, plus the letter
viewport/menu evidence. A `nativeHandled` log alone is insufficient. This direct
native action is not proof of a player walking to or clicking the mailbox.
Close the letter through existing process-local input and observe that no menu
remains; never focus the game or use global desktop input.

```powershell
& $sdvkit project review command 'progression_probe_finish' --json
& $sdvkit project review progression --npc Leah --mail SDVKit.ProgressionProbe_RainBarrel --quest SDVKit.ProgressionProbe_CheckBarrel --json
```

The original objective callback is synthetic, but invokes native `Quest.accept`
and `Quest.questComplete`. Require exactly one match, accepted count 1 and
completed count 1 through CLI and MCP. The native 25-Gold reward remains unclaimed
so the completed log entry is retained; no reward payment is claimed. The mod's
save checkpoint prevents redelivery but is not an independent success observation.

## Save, restart twice and clean up

Before stopping, follow the existing [config export/restage checks](gmcm-authoring.md#validate-export-stop-and-restage-the-saved-config).
Copy the exact accepted export to an explicitly selected ready directory made
from the same mod ZIP. Compare complete non-config files and DLL hash with the
original ready artifact and retain both config hashes. Never let a source rebuild
substitute for these preserved bytes. Keep the accepted `RequiredFriendshipPoints=500`.

Save through `stardew_fixture_save` on a server explicitly started with
`--allow-fixture-actions`, or the existing `sdvkit fixture save` command. Require
actual completion, exact fixture/save IDs and persisted timestamp. Close the MCP
client, stop the exact review **without reset**, and verify its process exited.
Start the selected accepted-config ready artifact against the same test save.
Require a new PID/start time/launch, unchanged fixture/farmer, same DLL hash and
accepted config bytes. `SaveLoaded` reevaluates the condition automatically.

Capture the selected progression through CLI and a new native MCP client: receipt
true, mailbox count 0, one accepted/completed quest, and no new effects. Require
`PROGRESSION_PROBE_RETAINED` and repeat `progression_probe_evaluate`; capture again.
Save, stop and start a second new process with the same selection, then repeat
these observations. Do not rerun preparation or finish on reload. Friendship may
decay during the save's overnight transition; record actual raw points rather
than silently forcing them back to 500.

Finish with diagnostics, confirmed exact `project review stop` and
`project review reset`, empty staging/mailboxes, acquirable locks, no unexpected
reparse points and a protected normal-data comparison. Preserve failed attempts.
Report compile/tests, native effects, two process reloads and cleanup separately.

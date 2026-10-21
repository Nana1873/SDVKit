# NPC, mail and quest read contract

This page keeps the read-only contract of `project review progression` and
`stardew_progression_get`. The live steps (diagnose one wrong friendship threshold in the
original [ProgressionProbe source](examples/progression-recipe/ProgressionProbe/ModEntry.cs),
correct its configuration, observe its mail and quest effects, place the villager and prove
persistence after two actual process restarts) are in
[Test an NPC, mail and quest read](test-a-progression-read.md) and run as
[`scripts/acceptance/progression.ps1`](../scripts/acceptance/progression.ps1). The sample uses the
installed Leah NPC and original rain-barrel mail/quest text; it introduces no NPC enumeration
or general progression editor. The first acceptance is in
[#235](https://github.com/Nana1873/SDVKit/issues/235).

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

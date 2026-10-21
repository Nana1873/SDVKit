# Test an NPC, mail and quest read

Use this pattern when your mod changes what an NPC, a letter or a quest does and you want to prove the effect, and that it is still there after a restart, with SDVKit's read-only progression observation instead of waiting for schedules. The complete worked example is [`scripts/acceptance/progression.ps1`](../scripts/acceptance/progression.ps1) with the mod in [`docs/examples/progression-recipe/ProgressionProbe`](examples/progression-recipe/ProgressionProbe/): it delivers one letter and one quest once Leah's friendship reaches a configured threshold, and its default threshold (750) is deliberately wrong.

Read the [progression contract](progression-authoring.md#read-only-contract) first: `project review progression --npc <id> --mail <id> --quest <id>` (MCP `stardew_progression_get`) reads the selected facts only and works in an unbound `single` test-save review. It never changes progression.

## 1. Diagnose the wrong condition

Build and package the sample with its default config, then start it with `--test-save`. Write the config in the bytes SMAPI keeps after `ReadConfig` (no BOM, CRLF, no final newline), or SDVKit reports the startup re-serialization as a config change you must reconcile first. Seed the state with the sample's `progression_probe_prepare` console command (synthetic: exactly 500 points) and read the selection: Leah `available` with 500 points, mail not received, tomorrow false, mailbox 0, quest matches 0. `progression_probe_evaluate` must log `PROGRESSION_PROBE_BLOCKED ... required=750`, and the read must stay unchanged. A delivered command alone proves neither the failure nor an effect.

## 2. Correct it and observe each effect separately

`progression_probe_correct` writes 500 into the config; accept it with `project review config-reconcile --mod <id>` and require `state=reconciled` and an export holding 500. Evaluate: the read must show exactly one mailbox entry and one incomplete quest (`mail=False/False/1`, `quest=1/0/0`), and a second evaluation must add nothing (`PROGRESSION_PROBE_RETAINED`).

Enter the Farm with `stardew_fixture_farm` and run `progression_probe_open_mail`, which calls the native Farm mailbox action: require the letter on screen (a `LetterViewerMenu` in the menu read) and `received=true` with an empty mailbox, then close it with `Escape`. `progression_probe_finish` runs the objective callback through native quest acceptance and completion: require accepted 1 and completed 1.

## 3. Place the villager instead of waiting for her schedule

Warp next to a free Farm tile from a world-area read and call `stardew_fixture_npc_place` for Leah on it (`--allow-fixture-actions`); the game decides whether a character fits, so the script tries a few tiles and keeps the first it accepts. Require her on that tile in the runtime read's `npcs` list. Talk to her with `stardew_world_act` `check` on her tile (`--allow-world-actions`), close the dialogue by clicking it, and require her friendship points to rise.

## 4. Save and restart twice

Save with `stardew_fixture_save`, stop **without** reset, and start again with the accepted config: copy the reconciled export over the extracted package's `config.json` and compare the manifest and DLL with the first extraction. Every progression read must match the saved state: Leah's exact points, mail received, mailbox 0, quest 1/1/1. An evaluation must log `PROGRESSION_PROBE_RETAINED` and add nothing. Save, stop and repeat once more, then finish with stop and reset.

## Not covered by the script

- The preparation, the objective callback and the mailbox action are synthetic helpers, so a pass does not prove a player walking to the mailbox, finishing the real objective or friendship gained any other way than one talk.
- Missing-NPC and no-friendship states, the MCP contract, other NPCs, multiplayer, dialogue, schedules and special orders. The contract and its limits are in [the progression contract](progression-authoring.md).
- Friendship decay over a day change: the fixture save does not advance the day, so the script compares exact points.

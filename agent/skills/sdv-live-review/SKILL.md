---
name: sdv-live-review
description: Run a Stardew Valley mod in SDVKit's isolated live lab and gather evidence - start a review, wait until it is ready, observe state, act through process-local input, take screenshots, and stop and reset cleanly. Use whenever a mod has to be tried in the actual game.
---

# Live review

## Before starting

- Run from the lab root. Check `.\sdvkit.cmd project review status --json` and `.\sdvkit.cmd project review status --topology network-2 --json`. If either shows a running review that another task or person started, wait for it to stop; never stop someone else's game.
- Decide what must be proven, and pick the smallest setup: plain `single` by default; `--test-save` for a disposable world (`sdv-test-world`); `--topology network-2` only for multiplayer (`sdv-multiplayer`).

## Start and wait

```powershell
.\sdvkit.cmd project review start .\workspaces\MyMod --json
.\sdvkit.cmd project review status --json
```

Poll `status` every 5 seconds. It is ready when `lab.gameSide.state` is `active`, `lab.gameSide.projectMod.phase` is `loaded` with `loadConfirmed` `true`, and, with `--test-save`, `lab.gameSide.testSave.phase` is `passed`. Loading usually takes 10 to 60 seconds. If a problem code appears, or nothing is ready after 3 minutes, report the status; do not retry blindly.

Without `--test-save` the game stays at the title screen (`worldReady` `false`). That is enough to prove that SMAPI loads the mod; anything in the world needs `--test-save`.

## Observe

`project review call <tool>` prints one tool's JSON result: exit 0 is success, exit 3 is a failure with the reason in the JSON.

- `stardew_runtime_get`: location, tile, time, money, selected item, menu open.
- `stardew_menu_get`: the open menu, its components and their screen bounds, and a `uiRevision`.
- `stardew_inventory_get`, `stardew_container_get`, `stardew_world_area_get`: items, chests, soil, crops, machines.
- `stardew_screenshot_capture '{"mode":"viewport","label":"before-click"}'`: a PNG in the lab profile's `Screenshots` folder. Labels are 1-64 letters, digits, `-` or `_`, and must be new each time. Open the image to judge visuals.
- `.\sdvkit.cmd project review diagnostics --mod <UniqueID> --json`: the mod's warnings and exceptions from the SMAPI log.

## Act

Add `--allow-input` to input tools:

```powershell
.\sdvkit.cmd project review call stardew_input_press '{"button":"E"}' --allow-input
.\sdvkit.cmd project review call stardew_input_click '{"x":640,"y":360,"button":"MouseLeft","count":1,"uiRevision":"<from stardew_menu_get>"}' --allow-input
```

- Take click coordinates from `stardew_menu_get` component bounds when possible, otherwise from a fresh screenshot. Never reuse coordinates from another run.
- Use a fresh `uiRevision` for every click, drag or chord.
- Close a menu with one `Escape` press or one click on its close button, then read the menu again. Do not use controller `B` as a fallback.
- Walking is a held key: `stardew_input_chord '{"buttons":["D"],"durationTicks":30,"uiRevision":"..."}'`.
- Run the mod's own console commands with `.\sdvkit.cmd project review command "<line>" --json`.

An input result proves delivery only. Read the state again, or take a new screenshot, to prove the effect. Never repeat an action that reported `mayHaveRun` or timed out; inspect first.

## Finish

```powershell
.\sdvkit.cmd project review stop --json
.\sdvkit.cmd project review reset --topology single --json
```

Stop is complete when its top-level `state` is `stopped` and `stagingRemoved` is `true`; an embedded `lab.gameSide.state` of `exiting` is normal. Reset is needed after `--test-save` and for `network-2`; it restores the disposable world. Report build, SMAPI load, behavior and visuals separately, with the evidence for each.

References: {{docs}}/live-review.md, {{docs}}/mcp.md, {{docs}}/runtime-state.md, {{docs}}/menu-inspection.md

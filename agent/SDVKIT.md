# SDVKit lab guide

This folder is an SDVKit **lab root** for Stardew Valley mod development, set up by SDVKit {{version}}. `sdvkit lab init` keeps this file current; put your own instructions in `CLAUDE.md` or `AGENTS.md`, not here.

SDVKit has two halves: a **modding toolkit** (create, check, build, package) and an **isolated live test lab** (run the game with your mod in its own profile, observe it, act in it, and clean up). Full documentation: {{docs}}/README.md

## Layout

- `workspaces/<ModName>/`: one folder, and usually one git repository, per mod. Respect each mod's own instructions.
- `.sdvkit/`: the installed SDVKit (`.sdvkit/tools/`) and everything SDVKit generates for this lab: isolated game profiles, staged mods, logs, screenshots, test saves. It is ignored by git. Each mod's build and package output goes to that mod's own `.sdvkit/`.
- `sdvkit.cmd`: runs the SDVKit installation that set up this lab. Always run it from this folder, and pass an explicit mod path. From PowerShell call it as `.\sdvkit.cmd`, from Git Bash as `./sdvkit.cmd`.

## Safety rules

1. Never write to the normal Stardew `Saves` folder or to the game's or a mod manager's `Mods` folder. SDVKit stages mods into its own isolated profile; nothing needs to be copied there by hand. A hook in `.claude/` blocks these writes for Claude Code.
2. Never copy game files, `*.xnb` content, personal saves or secrets into a mod repository.
3. Use only SDVKit's process-local input for the game. Never move the physical mouse, change window focus, or use desktop automation.
4. One live lab at a time. Before starting, check `.\sdvkit.cmd project review status --json`. `state` `stopped` means the lab is free. Any other state means a review is running: `root` names the reviewed mod and `lab.launchId` the launch. If you did not start it in this task, wait for it to stop; never stop someone else's game. Always finish with stop, and with reset for test-save or network reviews.
5. Claim only what was proven, and say which level: **build** succeeded, **SMAPI loaded** the mod, **behavior** was observed in the game, or **visuals** were checked on a screenshot.
6. Never repeat an action whose result says `mayHaveRun` or that timed out. Inspect the current state first.

## The usual loop

```powershell
.\sdvkit.cmd doctor --json
.\sdvkit.cmd project create smapi-mod .\workspaces\MyMod --name "My Mod" --author "Me" --unique-id "Me.MyMod" --description "What it does." --json
.\sdvkit.cmd project check .\workspaces\MyMod --json
.\sdvkit.cmd project build .\workspaces\MyMod --json
.\sdvkit.cmd project review start .\workspaces\MyMod --json
.\sdvkit.cmd project review status --json
.\sdvkit.cmd project review call stardew_runtime_get
.\sdvkit.cmd project review diagnostics --mod Me.MyMod --json
.\sdvkit.cmd project review stop --json
```

`project build` gives quick compile feedback; `project review start` builds and packages a source mod again by itself in isolation, so a separate build is not required before a review.

`project review start` returns within seconds, before the game is ready. Poll `status` every 5 seconds until all of these hold:

- `lab.gameSide.state` is `active`;
- `lab.gameSide.projectMod.phase` is `loaded` and `lab.gameSide.projectMod.loadConfirmed` is `true`;
- with `--test-save` only: `lab.gameSide.testSave.phase` is `passed`.

Loading usually takes 10 to 60 seconds. If `problems` is not empty, or nothing is ready after 3 minutes, stop and report the status instead of retrying. In PowerShell:

```powershell
$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 5
    $status = .\sdvkit.cmd project review status --json | ConvertFrom-Json
    $mod = $status.lab.gameSide.projectMod
} until (($status.lab.gameSide.state -eq 'active' -and $mod.phase -eq 'loaded' -and $mod.loadConfirmed) -or
    @($status.problems).Count -gt 0 -or (Get-Date) -gt $deadline)
```

`lab.gameSide.loadedMods.mods` lists every mod SMAPI loaded, which is further evidence for a load claim. `project review diagnostics --mod <UniqueID> --json` reports the mod's warnings and exceptions in `diagnostics`; an empty list means none were found in the scanned part of the SMAPI log.

A plain review without `--test-save` stays at the title screen: `stardew_runtime_get` reports `worldReady` `false` with a menu open. That is expected. For anything that needs a loaded farm, use `--test-save` (skill `sdv-test-world`); never load other saves.

`stop` is complete when its top-level `state` is `stopped` and `stagingRemoved` is `true`. The embedded `lab.gameSide.state` may still read `exiting`; that is normal. A fresh `status` then reports `stopped`.

## Acting in the game

- `project review command "<line>"` runs **your mod's or SMAPI's** console command. SDVKit's own `sdvkit ...` lines are rejected there.
- `project review call <tool> ['<json>']` runs one SDVKit tool and always prints JSON (it takes no `--json` flag): reads such as `stardew_runtime_get`, `stardew_menu_get`, `stardew_inventory_get`, `stardew_screenshot_capture`, and, with the matching opt-in flag, actions:
  - `--allow-input`: `stardew_input_press`, `chord`, `click`, `drag`, `scroll`, `text`, `cursor_set`, `cursor_clear`.
  - `--allow-fixture-actions` (test save): fixtures, `stardew_fixture_save`, and `stardew_lab_set_time`, `set_weather`, `set_season`, `warp`, `set_zoom`, `set_window`.
  - `--allow-world-actions`: `stardew_world_interact` (water, harvest, machine insert and collect).
- `.\sdvkit.cmd project review mcp serve --help` lists every tool. JSON arguments need PowerShell 7.3 or later; Windows PowerShell 5.1 strips their quotes.

An input result proves only that the input was delivered. Read the state again, or capture a screenshot, to prove the effect.

## Skills

Task skills live in `.claude/skills/` (Claude Code) and `.agents/skills/` (Codex):

| Skill | Use it to |
| --- | --- |
| `sdv-new-mod` | create a SMAPI C# mod or a Content Patcher pack in `workspaces/` |
| `sdv-build-package` | check, build and package a mod for release |
| `sdv-smapi-csharp` | develop and test a C# mod in the live lab |
| `sdv-content-patcher` | develop and diagnose a Content Patcher pack in the live lab |
| `sdv-live-review` | start, observe, act in and stop a live review, with evidence |
| `sdv-test-world` | use a disposable test world: fixtures, time, weather, warps, world actions, saving |
| `sdv-gmcm` | test a Generic Mod Config Menu integration |
| `sdv-multiplayer` | test with a network host and farmhand, or local split-screen |
| `sdv-diagnose` | find out why a mod misbehaves: logs, Data, maps, textures, CP patches, saves |
| `sdv-project-smoke` | run the automated load-and-tick smoke test for a C# mod |

`KNOWLEDGEBASE.md`, when present, collects SMAPI 4 and Stardew 1.6 pitfalls.

## MCP

MCP is optional; `project review call` reaches the same tools. To attach Claude Code to a running review, start the review first, then use `.sdvkit/agent/claude-mcp.json` as a template (see {{docs}}/mcp.md). The server ends when the review stops.

---
name: sdv-smapi-csharp
description: Develop and test a SMAPI C# mod for Stardew Valley in SDVKit's isolated live lab - edit, build, run it in the game, trigger its behavior, read its log, and fix. Use for C# mod work beyond creating the project.
---

# Develop a SMAPI C# mod

## Loop

1. Edit the source in `workspaces/<ModName>/`. Check `KNOWLEDGEBASE.md` for SMAPI 4 and Stardew 1.6 pitfalls before touching events, Harmony, multiplayer sync or assets.
2. `.\sdvkit.cmd project check <path> --json`, then `project build <path> --json`. Fix compile errors first.
3. Quick load test: `sdv-project-smoke` proves that SMAPI loads the mod and the game ticks with it.
4. Behavior test: start a review (see `sdv-live-review`):

   ```powershell
   .\sdvkit.cmd project review start .\workspaces\MyMod --json
   .\sdvkit.cmd project review status --json
   ```

   Add `--test-save` for a disposable world (`sdv-test-world`), and `--companion <path>` for each dependency the mod needs, such as Generic Mod Config Menu. SDVKit never searches the normal `Mods` folder for dependencies.
5. Trigger the behavior:
   - a console command the mod registers: `.\sdvkit.cmd project review command "mymod_debug on" --json`
   - game input or world actions through `project review call` (see `sdv-live-review`)
6. Read the result:
   - `.\sdvkit.cmd project review diagnostics --mod Me.MyMod --json` for the mod's warnings and exceptions
   - state reads such as `call stardew_runtime_get`, `stardew_inventory_get`, `stardew_menu_get`
   - `call stardew_screenshot_capture '{"mode":"viewport","label":"after-fix"}'` for visuals
7. To load a code change, stop and start the review again. A running game does not pick up a rebuilt DLL.
8. Finish with `project review stop --json`, plus `reset` when the review used `--test-save`.

## Good practice for test hooks

A debug console command in the mod (for example `mymod_state` printing its internal state to the log, or `mymod_trigger` forcing an event) makes behavior easy to trigger and observe through `project review command`. Keep such commands harmless and clearly named.

## Report

Separate build, SMAPI load, observed behavior and visual result. Quote the diagnostics or state that proves each claim.

References: {{docs}}/smapi-authoring.md, {{docs}}/i18n-authoring.md, {{docs}}/data-migration-authoring.md

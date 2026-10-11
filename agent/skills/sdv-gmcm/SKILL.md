---
name: sdv-gmcm
description: Test a Stardew Valley mod's Generic Mod Config Menu integration in SDVKit's live lab - open the mod's config page, change options with process-local clicks and drags, save, verify the mod reads the new values, and accept the changed config.json. Use for any GMCM-related work.
---

# Test a GMCM integration

## Needs

- Generic Mod Config Menu as an explicitly selected companion folder outside the game's `Mods` folder, for example `.sdvkit/providers/GenericModConfigMenu`. SDVKit's recipes were verified with GMCM 1.16.0.
- A console command in the mod that opens its GMCM page, and one that logs the current config values. Both make the test observable without hunting through the title-screen menu.

## Steps

1. Start with the provider: `.\sdvkit.cmd project review start .\workspaces\MyMod --companion .\.sdvkit\providers\GenericModConfigMenu --test-save --json`, then wait until ready (`sdv-live-review`).
2. Open the page: `.\sdvkit.cmd project review command "mymod_config" --json`.
3. Locate controls. GMCM's custom menu exposes only partial data through `stardew_menu_get`, so capture a viewport screenshot and read control positions from the image. Screenshot pixels are UI coordinates at UI scale 1.
4. Change values with `stardew_input_click` (checkboxes, buttons) or `stardew_input_drag` (sliders) and `--allow-input`, each with a fresh `uiRevision` from `stardew_menu_get`. Screenshot after each change.
5. Before saving, check that the mod still reports the old value. GMCM only caches edits until Save.
6. Click Save. The click itself may end with exit 3 and `reviewStagingOwnershipDrifted`: GMCM wrote `config.json` during the click, and SDVKit refuses to report on changed staged files. That is expected. Do not repeat the click.
7. Accept the change, then confirm the mod sees it:

   ```powershell
   .\sdvkit.cmd project review config-reconcile --mod Me.MyMod --json
   .\sdvkit.cmd project review command "mymod_state" --json
   ```

   `config-reconcile` exports the accepted config below `.sdvkit/`; the mod's source config is not changed.
8. For a value that applies on load, stop and start again and check the effect in the new process.
9. Finish with stop and reset.

References: {{docs}}/gmcm-authoring.md, {{docs}}/gmcm-number-authoring.md, {{docs}}/live-review.md

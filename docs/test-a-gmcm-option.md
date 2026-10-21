# Change and verify a GMCM option

Use this pattern to prove that an option in your mod's Generic Mod Config Menu page is saved and takes effect. Two complete worked examples: [`scripts/acceptance/gmcm-number.ps1`](../scripts/acceptance/gmcm-number.ps1) tests an integer slider from [`docs/examples/gmcm-number-recipe`](examples/gmcm-number-recipe/GmcmArrivalRow/), and [`scripts/acceptance/gmcm-checkbox.ps1`](../scripts/acceptance/gmcm-checkbox.ps1) tests a Boolean checkbox from [`docs/examples/gmcm-checkbox-recipe`](examples/gmcm-checkbox-recipe/GmcmArrival/). The [GMCM contract and API notes](gmcm-authoring.md) list what the checkbox example does and does not cover.

## Prepare GMCM and your config

- Copy GMCM into your lab and pass the copy with `--companion`. SDVKit does not search your normal `Mods` folder for it.
- GMCM writes its own `config.json` on first launch. Write its default file into your copy before the review, or SDVKit reports the staged files as changed.
- Ship your mod's default `config.json` in exactly the bytes SMAPI writes back: UTF-8 without BOM, CRLF line endings, no final newline. Otherwise SMAPI rewrites it at startup and SDVKit reports `reviewStagingOwnershipDrifted`.
- Register with `titleScreenOnly: false` if the option should be changed in a loaded save. A test console command that opens your page (`api.OpenModMenu(ModManifest)`) and one that logs the current value make the test much easier.

## 1. Read the default

Start a `single` review with `--test-save`, wait until it is ready, then log the current value and check its effect in the game.

## 2. Change the option

GMCM's page has no menu components SDVKit can read, so you work from a screenshot:

```powershell
& $sdvkit project review call stardew_screenshot_capture '{"mode":"viewport","label":"gmcm-open"}'
& $sdvkit project review menu --json
```

Measure the control's position on the screenshot; a checkbox needs one click, and a checked box is only cached until Save, so log the value and read the staged `config.json` to prove it is unchanged. The screenshot and the menu's `viewport` use the same coordinates. Click or drag with a fresh `uiRevision` from `menu`:

```powershell
& $sdvkit project review call stardew_input_drag '{"x":820,"y":141,"endX":1004,"endY":141,"button":"MouseLeft","durationTicks":6,"uiRevision":"..."}' --allow-input
```

A GMCM slider truncates the pointer position before clamping it, so drag slightly past the last tick to reach the maximum. Until you click Save, your mod's value must not change: log it after each move to prove that.

## 3. Save and accept the change

Click Save. GMCM rewrites your `config.json`, so the click or the next read ends with exit `3` and `reviewStagingOwnershipDrifted`. That is expected; do not click again. Wait until the staged `config.json` holds the new value, then accept it:

```powershell
& $sdvkit project review config-reconcile --mod My.Mod --json
```

The result's `reconciliation.exportPath` holds the saved file. Reads and input work again, and your mod should report the new value. See [configuration reconciliation](live-review.md#accept-an-intentional-configuration-change).

## 4. Prove the effect after a restart

Stop the review. Copy the exported `config.json` into a fresh copy of your packaged mod, start a new review with it, and check the effect. The example checks that the start-of-day warp lands on the saved row. Finish with stop and reset.

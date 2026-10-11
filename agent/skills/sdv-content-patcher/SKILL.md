---
name: sdv-content-patcher
description: Develop, test and diagnose a Content Patcher content pack for Stardew Valley in SDVKit's isolated live lab - check content.json offline, run it with Content Patcher, see which patches apply, read the resulting Data, and hot-refresh patch files. Use for any Content Patcher pack work.
---

# Develop a Content Patcher pack

## Needs

A Content Patcher installation that the user selects explicitly, as a folder outside the game's `Mods` folder (for example copied below `.sdvkit/providers/ContentPatcher`). SDVKit never takes it from the normal `Mods` folder. Diagnosis and refresh support Content Patcher 2.9.1.

## Loop

1. Edit `content.json` and its files in `workspaces/<PackName>/pack/`. Check `KNOWLEDGEBASE.md` for Content Patcher token and condition pitfalls.
2. Offline: `.\sdvkit.cmd project check .\workspaces\MyPack\pack --json`. It catches broken JSON, schema errors, missing `FromFile` files and malformed `Include` fragments.
3. Start the pack with its provider (content packs run in `single` only):

   ```powershell
   .\sdvkit.cmd project review start .\workspaces\MyPack\pack --topology single --companion .\.sdvkit\providers\ContentPatcher --json
   ```

   Poll `project review status --json` until the pack is loaded.
4. See what Content Patcher did:

   ```powershell
   .\sdvkit.cmd project review cp-diagnose --pack Me.MyPack --provider Pathoschild.ContentPatcher --asset Data/Objects --json
   ```

   It reports whether each patch loaded, whether its conditions match and whether it applied. Add `--order` to see the apply order of competing patches on that asset.
5. Read the effect: `.\sdvkit.cmd project review data get Data/Objects <key> --json`, or `map`, `texture` and `mod-assets` for other assets.
6. Change a patch file and refresh it without restarting:

   ```powershell
   .\sdvkit.cmd project review cp-refresh .\workspaces\MyPack\pack --pack Me.MyPack --provider Pathoschild.ContentPatcher --file assets/objects.json --observe-data Data/Objects --key <key> --json
   ```

   Changes to the manifest or non-patch fields of `content.json` need stop, reset and start instead.
7. Finish with `project review stop --json`.

## Report

Diagnosis shows what Content Patcher decided. The Data, map or texture read shows the result. Neither proves how it looks in the game; capture a screenshot for that.

References: {{docs}}/cp-authoring.md, {{docs}}/cp-diagnosis.md, {{docs}}/cp-refresh.md, {{docs}}/inspection.md

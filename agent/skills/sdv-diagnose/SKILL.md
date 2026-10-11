---
name: sdv-diagnose
description: Find out why a Stardew Valley mod misbehaves using SDVKit - read the mod's SMAPI warnings and exceptions, inspect the game's canonical Data, maps, textures, audio and mod assets after all content edits, check Content Patcher decisions, and inspect isolated save copies. Use when a mod fails to load, throws errors, or shows the wrong content.
---

# Diagnose a mod

Start from the symptom and read only what explains it.

| Symptom | First read |
| --- | --- |
| Fails to build or package | `project check <path> --json`, then the build output |
| Not loaded, or errors at startup | `project review status --json` (problem codes, `projectMod`), then `project review diagnostics --mod <UniqueID> --json` |
| Exceptions while playing | `project review diagnostics --mod <UniqueID> --limit 50 --json` |
| Wrong item, recipe, shop or NPC data | `project review data get <asset> <key> --json` |
| Wrong map or tile | `project review map --help`, or `call stardew_map_tile_get` |
| Wrong image | `project review texture --help`; `call stardew_texture_preview` returns a PNG |
| A Content Patcher edit has no effect | `project review cp-diagnose --pack <id> --provider Pathoschild.ContentPatcher --asset <asset> --json` (see `sdv-content-patcher`) |
| Which assets a mod provides | `call stardew_mod_assets_list` |
| A value in a save | `.\sdvkit.cmd save inspect --fixture work --json` for the test world, or `--source <exact-save-file>` for an explicitly chosen save copy |

## Rules

- Diagnostics attribute log lines to a mod. Attribution is a hint, not proof of the cause. Confirm the cause with a state read or a fix that changes the result.
- Data, map and texture reads show the game's final content after every edit from every mod. Compare with what the mod intended to change.
- Map, texture, audio and mod-asset reads need a plain `single` review. Data and menu reads also work for one network role or a local screen.
- Never open or edit the user's real save. `save inspect --source` reads an explicitly selected file; import a real save only as an isolated copy.
- After a fix, rebuild, restart the review, and read the same evidence again.

References: {{docs}}/inspection.md, {{docs}}/live-review.md, {{docs}}/save-inspection.md, {{docs}}/cp-diagnosis.md

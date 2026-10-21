---
name: sdv-multiplayer
description: Test a Stardew Valley C# mod in multiplayer with SDVKit - a network host and farmhand in two isolated game processes, or a local split-screen farmhand in one process - including leaving and rejoining. Use when the user asks about multiplayer, co-op, farmhands or split-screen.
---

# Test multiplayer

Two setups exist. Use only what the question needs.

## Network host and farmhand (`network-2`)

Two game processes, one host and one farmhand, each with its own isolated profile. C# mods only.

1. Automated check that both load and stay joined: `.\sdvkit.cmd project smoke .\workspaces\MyMod --topology network-2 --json`.
2. Interactive: `.\sdvkit.cmd project review start .\workspaces\MyMod --topology network-2 --json`. Wait until `.\sdvkit.cmd project review status --topology network-2 --json` shows both roles ready and the pair joined.
3. Every role-specific command takes `--topology network-2 --role host` or `--role farmhand`:

   ```powershell
   .\sdvkit.cmd project review command "mymod_state" --topology network-2 --role farmhand --json
   .\sdvkit.cmd project review call stardew_inventory_get --topology network-2 --role host
   ```

4. Leave and rejoin the farmhand:

   ```powershell
   .\sdvkit.cmd project review network leave --role farmhand --json
   .\sdvkit.cmd project review network status --role host --json
   .\sdvkit.cmd project review network join --role farmhand --json
   ```

   Wait for the pair to pass again before more checks. A farmhand MCP client or `call` binding from before the rejoin is no longer valid.
5. Finish with `project review stop --topology network-2 --json` and `project review reset --topology network-2 --json`.

Only the host changes world state with fixture and lab tools; the farmhand can warp, zoom, resize, enter and leave buildings.

## Local split-screen

One process with a second local player, inside a single `--test-save` review:

```powershell
.\sdvkit.cmd project review split-screen join --json
.\sdvkit.cmd project review split-screen status --screen 1 --json
.\sdvkit.cmd project review call stardew_input_press '{"button":"E"}' --screen 1 --allow-input
.\sdvkit.cmd project review split-screen leave --screen 0 --json
```

Use the screen IDs that `status` reports. Join and leave run on the host screen (`--screen 0` or none). A mod console command picks a screen with SMAPI's `screen=<id>` argument.

## Report

State which roles or screens showed which behavior, and keep host and farmhand evidence apart.

References: {{docs}}/test-a-multiplayer-mod.md, {{docs}}/multiplayer-authoring.md, {{docs}}/live-review.md

---
name: sdv-test-world
description: Use SDVKit's disposable test world to set up exact game situations for a Stardew Valley mod - fixture buildings, animals and objects, time, weather, season, warps, zoom, fishing preparation, watering, harvesting, machines, chest transfers, saving and reloading. Use when a test needs a specific world state or must survive a save.
---

# Disposable test world

The test world is an SDVKit-owned save copy. Each review changes a work copy; `reset` restores it. Your real saves are never touched.

## Prepare and start

```powershell
.\sdvkit.cmd lab test-save --topology single --json
.\sdvkit.cmd project review start .\workspaces\MyMod --test-save --json
```

`lab test-save` creates the baseline once, while no review runs. Wait until `status` shows `lab.gameSide.testSave.phase` `passed` (see `sdv-live-review`). The fixture starts inside the FarmHouse.

## Shape the world (`--allow-fixture-actions`)

```powershell
.\sdvkit.cmd project review call stardew_fixture_farm --allow-fixture-actions
.\sdvkit.cmd project review call stardew_lab_set_time '{"time":1930}' --allow-fixture-actions
.\sdvkit.cmd project review call stardew_lab_set_weather '{"weather":"Rain"}' --allow-fixture-actions
.\sdvkit.cmd project review call stardew_lab_warp '{"location":"Town","x":20,"y":30}' --allow-fixture-actions
```

- `stardew_fixture_farm` and `stardew_fixture_enter` move through natural doors; `stardew_lab_warp` jumps to a permanent location.
- `stardew_fixture_building_ensure`, `animal_ensure`, `object_ensure` and `object_clear` place SDVKit-owned buildings, animals and objects.
- `stardew_lab_set_season`, `set_zoom` and `set_window` adjust the rest; `stardew_fixture_fishing_prepare` stands the player at water with a rod and `stardew_fixture_fishing_catch` lands the game's fish for a cast bobber (setup: the minigame is skipped); `stardew_fixture_item_add` puts an item (food, a gift, ingredients, a modded item) into an empty backpack slot; `stardew_fixture_npc_place` brings a villager next to the player (find villagers in `stardew_runtime_get` `npcs`).
- Time, weather, season, objects and fishing need the host or single player.

## Act on the world

- `stardew_world_interact` with `--allow-world-actions` waters, harvests, or fills and empties a machine on the faced adjacent tile. Read `stardew_world_area_get` and `stardew_inventory_get` first; the action needs their fresh revisions.
- `stardew_world_act` with `--allow-world-actions` presses the action button (`check`) or uses the selected tool (`useTool`) on one of the four tiles next to the player: open a chest, read a sign, talk, hoe or mine. It needs only a fresh `inventoryRevision`.
- `stardew_world_move '{"direction":"down"}'` with `--allow-world-actions` walks exactly one tile and reports `tileX`/`tileY` and `reached`; `"turnOnly":true` only faces the direction. A blocked tile is refused with `worldMoveTargetBlocked`. There is no pathfinding: plan the route from `stardew_world_area_get`, one move at a time, or use `stardew_lab_warp` for setup.
- `stardew_container_transfer` with `--allow-container-transfer` moves items between an open chest and the backpack.
- Select a hotbar slot with `stardew_input_press '{"button":"D3"}' --allow-input`.

Read the world and inventory again after every action. `completed` means the input finished, not that the effect happened.

## Save and reload

`stardew_fixture_save` with `--allow-fixture-actions` saves and returns only after the save completes. To test persistence: save, `project review stop` without reset, start again with `--test-save`, read the state. End with `stop` and `reset`.

References: {{docs}}/lab-reference.md, {{docs}}/world-interaction.md, {{docs}}/container-transfer.md, {{docs}}/mcp.md

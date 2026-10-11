# Glossary

These terms appear across the SDVKit documentation. Each is defined once here. Other pages use them without repeating the definition.

## Places

- **Lab root**: The folder you run live commands from. Its `.sdvkit/` subfolder holds everything SDVKit generates for live tests: isolated game profiles, staged mods, logs, screenshots and test saves. Run every live command from the same lab root. See [choose a mod workspace](toolkit.md#choose-a-mod-workspace).
- **Mod workspace**: One mod's source folder, usually `workspaces/<ModName>/` below the lab root. Its own `.sdvkit/` holds that mod's build and package output.
- **Isolated profile**: The game data folder SDVKit gives each launch below the lab root. SDVKit never writes to your normal `Saves` folder or to your normal or mod-manager `Mods` folder.
- **Staging**: Copying the reviewed mod, its companions and SDVKit's game-side mod into the isolated profile's `Mods` folder for one review. A single review's stop removes it (`stagingRemoved`); a `network-2` review keeps it for a restart until `project review reset`.

## Reviews and ownership

- **Review**: One live run of the game with your mod staged, started by `project review start` and ended by `project review stop`. A **smoke** (`project smoke`) is an automated review that loads the mod, ticks the world and tears down by itself.
- **Launch**: One start of the game processes for a review, identified by `lab.launchId`. Results from an earlier launch never count for a later one.
- **Owned**: Belonging to the review this lab started. SDVKit writes an ownership marker at start and checks it before every action, read and cleanup. It refuses to act on a game, folder or file it does not own.
- **GameSide**: SDVKit's own SMAPI mod, staged into every review. It reports state, runs commands on the game thread and delivers process-local input.
- **Companion** and **provider**: Other mods you select explicitly for a review. A provider is the companion that a content pack needs, such as Content Patcher. SDVKit never picks them from a mod manager's folder.
- **Build identity**: A hash of the staged file set. It tells you which files were staged, not what the game loaded into memory.

## Topology, roles and screens

- **Topology**: How many game processes a review runs. `single` is one game. `network-2` is two local games: a **host** and one **farmhand**, joined over the network. Passing `network-2` does not prove general multiplayer compatibility.
- **Role**: Which process of a `network-2` review a command addresses: `host` or `farmhand`. In `single` there is one role.
- **Screen**: One player of local split-screen inside a single game process. Screen `0` is the main player. Split-screen needs a `single` review with `--test-save`.
- **Bound** and **unbound**: An MCP server started with `--role` or `--screen` is bound to that role or screen for its whole life. An unbound server addresses the single review's main player. Some tools exist only on unbound servers.

## Worlds and fixtures

- **Test save**, also **disposable world** or **fixture**: A throwaway save that SDVKit creates once with `lab test-save`. A single review uses it with `--test-save`; a `network-2` review always uses it. You may change it freely: stop keeps the changed work save for a restart, and `project review reset` restores the baseline. Your own saves are never loaded; [save inspection](save-inspection.md) reads an isolated copy offline.
- **Fixture action**: A command that prepares the test save: place objects, buildings or animals, warp, set time, weather or season, or save. Fixture actions need `--allow-fixture-actions` for MCP and work only in an owned test save.
- **World ready**: A save is loaded and the player can act (`worldReady` `true`). A review without `--test-save` stays at the title screen, where it is not world ready.

## Commands and results

- **Opt-in flag**: A flag that unlocks a group of actions for `project review call` and `mcp serve`: `--allow-input`, `--allow-fixture-actions`, `--allow-world-actions`, `--allow-container-transfer` and `--allow-cp-refresh`. Without it, only reads are available.
- **Process-local input**: Keyboard, mouse and controller input that GameSide injects into the game process. It never moves the physical cursor or changes window focus.
- **Revision**: A value a read returns, such as `uiRevision` or an inventory revision. An action that names a revision fails when the state has changed since that read. Read again, then act.
- **Bounded**: A read or result with a fixed size limit, such as at most 256 tiles or a capped log excerpt. A bounded result says when it was truncated.
- **`commandWritten`**: Whether a console command ran on the game thread: `true` it ran, `false` it did not, missing means delivery is unknown. It does not prove the command's effect. **`mayHaveRun`** on an action means the same uncertainty. Never repeat such a command or action automatically; read the state first.
- **Exit codes**: `0` success, `2` invalid syntax or arguments, `3` a controlled failure; read `problems`. See [read a result](README.md#read-a-result).

## Evidence levels

Claim only the level you proved:

1. **Build**: the mod compiled and packaged.
2. **Loaded**: SMAPI loaded the mod in the review (`projectMod.phase` `loaded` with `loadConfirmed`).
3. **Behavior**: a state read after the action shows the effect.
4. **Visual**: a screenshot shows the effect.

An input or action result alone proves only that the input was delivered.

## Assets

- **Canonical asset name**: The game's own spelling of an asset, such as `Data/Objects`, without locale suffix or file extension.

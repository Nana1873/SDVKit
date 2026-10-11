# SDVKit

An agent-first Windows toolkit for developing Stardew Valley mods, with an isolated live test lab.

- **Build mods:** inspect projects, create a SMAPI mod or Content Patcher pack, build, and package it.
- **Test in game:** smoke-test a mod, or keep a review running with companions, test saves, screenshots and optional agent control.

[Download](https://github.com/Nana1873/SDVKit/releases/latest) · [Changelog](CHANGELOG.md) · [Documentation](docs/README.md) · [Glossary](docs/glossary.md) · [Issues](https://github.com/Nana1873/SDVKit/issues)

This documentation follows `main` for **v2.0.0**. Install the [latest published release](https://github.com/Nana1873/SDVKit/releases/latest) with the README from its tag. For this version, see its [release status](https://github.com/Nana1873/SDVKit/issues/286) and [tagged documentation](https://github.com/Nana1873/SDVKit/tree/v2.0.0).

## Set up with your agent

Ask your coding agent to install SDVKit and prepare your workspace. Replace the folder placeholder below. The workspace folder holds your mod projects, the portable tools and the isolated live lab. Choose a folder outside your game installation and your normal `Mods` and `Saves` folders. The same prompt is in [SETUP_PROMPT.txt](SETUP_PROMPT.txt).

```text
Set up SDVKit from https://github.com/Nana1873/SDVKit for my Stardew Valley mod work.

Workspace / lab folder: <choose a mod-development folder>

Download the latest published Windows-x64 release and its SHA-256 sidecar,
verify the checksum, and extract it into a fresh versioned directory below the
workspace's .sdvkit/tools/ folder. Follow that release's README and documentation
to check the required .NET SDK and existing Stardew Valley / SMAPI installation.
If that release's `sdvkit.exe lab --help` lists `lab init`, run it with
`lab init <workspace> --json` to add SDVKit's agent layer (SDVKIT.md, task skills,
a safety hook, a sdvkit.cmd launcher), then read SDVKIT.md and follow it.
Otherwise follow that release's README for the workspace layout.
Keep existing mod projects at their selected paths and normal saves and Mods untouched.
Finish setup by running the installed sdvkit.exe doctor --json; report the installed
version, the lab root, the lab init result if any, and missing prerequisites.
Leave game launches and MCP connections for a requested mod review.
```

`lab init` gives Claude Code and Codex a guide, task skills and a hook. The hook blocks writes to the game folder and your normal `Saves`. Run `lab init` again after you install a new version. The agent uses the CLI for mod development and can [connect MCP to an active review](docs/mcp.md). See the [workspace layout](docs/toolkit.md#choose-a-mod-workspace) for several mods in one lab, or install by hand as below.

## Requirements

- Windows x64 (Windows 10 version 1709 or later for the live lab).
- The **.NET 8 SDK, version 8.0.419 or a later 8.0 SDK**, as selected by `global.json`. You need it to build mods and the game-side support mod, even with the portable ZIP.
- A local Stardew Valley installation with SMAPI, for building and live testing. `doctor` must find exactly one ready installation.
- Test-save automation accepts Stardew `>=1.6.15, <1.7` (file version `>=1.6.15.24356, <1.7`) and SMAPI `>=4.5.0, <5.0`. Runtime API checks also apply. These ranges do not claim that every version was tested.

See [2.x support, compatibility and upgrades](docs/support.md) for companion versions, capability limits and known risks. Project creation, inspection and offline authoring checks work without an installed game. Content-pack reviews need a local provider you select, such as Content Patcher.

## Install

Download the Windows-x64 ZIP and its `.sha256` file from the [latest published release](https://github.com/Nana1873/SDVKit/releases/latest). These examples use v2.0.0; for another version, follow its tagged README. In the download folder, compare the hash with the sidecar, then extract to a fresh folder:

```powershell
$archive = '.\SDVKit-2.0.0-win-x64.zip'
$expectedHash = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The download checksum does not match.'
}
Expand-Archive -LiteralPath $archive -DestinationPath .\SDVKit-install
$sdvkit = (Resolve-Path .\SDVKit-install\SDVKit-2.0.0-win-x64\sdvkit.exe).Path
& $sdvkit --help
& $sdvkit doctor --json
```

Keep `$sdvkit` as the absolute executable path in this PowerShell session. The examples run it from your project or lab folder; changing folder does not change which executable runs. You can also add the extracted folder to PATH and call `sdvkit` directly.

Before an upgrade, stop active labs and reviews and finish their reset. Extract the new version to its own folder; do not replace a running package.

## Create and package a mod

For several mods in one lab, the optional [workspace layout](docs/toolkit.md#choose-a-mod-workspace) keeps each mod's source under `workspaces/<ModName>/` beside the lab's `.sdvkit/`. You can still choose any project path.

Run from the directory where you want to create `ExampleMod`:

```powershell
& $sdvkit project create smapi-mod .\ExampleMod --name 'Example Mod' --author 'ExampleAuthor' --unique-id 'ExampleAuthor.ExampleMod' --description 'My first mod.' --json
& $sdvkit project inspect .\ExampleMod --json
& $sdvkit project check .\ExampleMod --json
& $sdvkit project build .\ExampleMod --json
& $sdvkit project package .\ExampleMod --json
```

Build and package output stays under the mod project's ignored `.sdvkit/`. The package result names the ZIP to distribute. See the [toolkit guide](docs/toolkit.md) for Content Patcher packs and diagnostics, or follow the [CP authoring recipe](docs/cp-authoring.md).

## Test a mod in the isolated lab

Run these commands from the folder whose ignored `.sdvkit/` should hold the lab. Target an existing C# mod project:

```powershell
& $sdvkit project smoke .\ExampleMod --topology single --json
```

A passing smoke confirms that SMAPI loaded the staged mod, the game-tick check completed, and the lab stopped and reset cleanly. It does not test every feature of the mod.

For a functional or visual check, start a persistent review:

```powershell
& $sdvkit project review start .\ExampleMod --test-save --json
& $sdvkit project review status --json
# Diagnose the selected mod, then exercise and observe its actual effect.
& $sdvkit project review diagnostics --mod ExampleAuthor.ExampleMod --json
& $sdvkit project review inventory --json
& $sdvkit project review stop --json
& $sdvkit project review reset --topology single --json
```

See [live reviews](docs/live-review.md) for test saves, companions, screenshots and local host/farmhand testing, including [two local screens in one process](docs/live-review.md#local-split-screen-review). [Native MCP](docs/mcp.md) connects an agent to an already-running review.

## Choose a workflow

| Need | Entry point | Supported target / scope |
| --- | --- | --- |
| Create, inspect, package | `project` | C# mods and content packs; C# build |
| Automated smoke | `project smoke` | Standalone C# mod; single or local host/farmhand |
| Functional/visual review | `project review` | C# in either topology; content-pack target in single |
| Inspect game content, inventories and the current world; move a chest quantity | `project review data/map/texture/audio/mod-assets/inventory/container/container-transfer/world` | Data, inventory, container and world reads support roles and local screens; other asset families are single-only. Transfer needs an unbound single review on a test save and the MCP opt-in |
| Agent observation/actions | `project review mcp serve` or `project review call <tool>` | Single or one fixed host/farmhand role; actions opt in |

The [capability matrix](docs/README.md#capability-matrix) lists fixture and role requirements.

## Isolation and evidence

Automatic operations never use your normal saves or your normal or mod-manager `Mods` folder. A save you select is read only as an isolated copy. Builds, lab profiles, test saves, logs and screenshots live below `.sdvkit/`. SDVKit reuses the installed game and SMAPI's `--mods-path`; Mod Organizer 2 is not required.

This is process and data isolation, not a Windows sandbox for arbitrary mod code. Build success, SMAPI load, functional behavior and visual acceptance are separate [evidence levels](docs/glossary.md#evidence-levels). SDVKit does not download missing mods or providers.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for source builds and checks, and the [release procedure](docs/releasing.md) for artifact verification and focused live acceptance.

The agent layer that `sdvkit lab init` installs lives in [agent/](agent/), including its [task skills](agent/skills/).

## Credits and license

Inspired by [skyrimvr-claude-toolkit](https://github.com/WingedGuardian/skyrimvr-claude-toolkit); the live-lab design also studies [StardewValley-MCP](https://github.com/luy-0/StardewValley-MCP) and [stardew-valley-ai-modkit](https://github.com/liminalwarmth/stardew-valley-ai-modkit).

[MIT](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)

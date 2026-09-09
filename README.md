# SDVKit

An agent-first Windows toolkit for developing Stardew Valley mods, with an isolated live test lab.

- **Build mods:** inspect projects, create a SMAPI mod or Content Patcher pack, build, and package it.
- **Test in game:** smoke-test a mod or keep a review running with selected companions, disposable worlds, screenshots, and optional agent control.

[Download](https://github.com/Nana1873/SDVKit/releases/latest) · [Changelog](CHANGELOG.md) · [Documentation](docs/README.md) · [Roadmap](https://github.com/Nana1873/SDVKit/issues/84)

This documentation follows `main` for **v0.10.0**. Check [release acceptance and publication status](https://github.com/Nana1873/SDVKit/issues/211) before installing; published packages have matching [tagged documentation](https://github.com/Nana1873/SDVKit/tree/v0.10.0).

## Set up with your agent

Ask your coding agent to install SDVKit and prepare your workspace. Replace the two folder placeholders below: the install folder holds the portable tools; the workspace folder holds your mod projects and isolated live lab. Choose folders outside your game installation, normal `Mods`, and `Saves` directories.

```text
Set up SDVKit from https://github.com/Nana1873/SDVKit for my Stardew Valley mod work.

Install folder: <choose a tools folder>
Workspace / lab folder: <choose a mod-development folder>

Download the latest published Windows-x64 release and its SHA-256 sidecar,
verify the checksum, and extract it into a fresh versioned install directory.
Follow that release's README and documentation to check the required .NET SDK
and existing Stardew Valley / SMAPI installation.
Use the workspace as the lab root, with new mod sources under workspaces/<ModName>/
and generated build/test data under the owning project's or lab's ignored .sdvkit/.
Keep existing mod projects at their selected paths and normal saves and Mods untouched.
Finish setup by running --help and doctor --json; report the installed version,
absolute executable path, lab root, and any missing prerequisites. Leave game
launches and MCP connections for a requested mod review.
```

The agent can use the CLI for mod development and [connect MCP to an active review](docs/mcp.md) when needed. See the [workspace layout](docs/toolkit.md#choose-a-mod-workspace) for several mods sharing one lab, or follow the manual installation steps below.

## Requirements

- Windows x64 (Windows 10 version 1709 or later for the live lab) and the **.NET 8 SDK, version 8.0.419 or a later 8.0 SDK**, as selected by `global.json`. The SDK is needed to build mods and the game-side support mod, even when using the portable ZIP.
- A local Stardew Valley installation with SMAPI for building and live testing. `doctor` must find exactly one ready installation.
- Disposable-world automation accepts Stardew `>=1.6.15, <1.7` (file version `>=1.6.15.24356, <1.7`) and SMAPI `>=4.5.0, <5.0`. Runtime API checks also apply; these ranges are not a claim that every version has been tested.

Project creation and inspection work without an installed game. Content-pack reviews need an explicitly selected local provider such as Content Patcher.

## Install

Once [v0.10.0](https://github.com/Nana1873/SDVKit/releases/tag/v0.10.0) is published, download `SDVKit-0.10.0-win-x64.zip` and its `.sha256` file. In the download directory, compare the hash with the sidecar, then extract to a fresh directory:

```powershell
$archive = '.\SDVKit-0.10.0-win-x64.zip'
$expectedHash = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The download checksum does not match.'
}
Expand-Archive -LiteralPath $archive -DestinationPath .\SDVKit-install
$sdvkit = (Resolve-Path .\SDVKit-install\SDVKit-0.10.0-win-x64\sdvkit.exe).Path
& $sdvkit --help
& $sdvkit doctor --json
```

Keep `$sdvkit` as the absolute executable path in this PowerShell session. The examples below use it from your chosen project/lab directory; changing directory does not change which executable runs. Alternatively add the extracted program directory to PATH and use `sdvkit` directly.

Before an upgrade, cleanly stop active labs/reviews and finish their required reset. Extract the new version separately; do not replace a running package.

## Create and package a mod

For several mods sharing one lab, the optional [workspace layout](docs/toolkit.md#choose-a-mod-workspace) keeps each mod's source under `workspaces/<ModName>/` beside the lab's `.sdvkit/`; explicit project paths remain your choice.

Run from the directory where you want to create `ExampleMod`:

```powershell
& $sdvkit project create smapi-mod .\ExampleMod --name 'Example Mod' --author 'ExampleAuthor' --unique-id 'ExampleAuthor.ExampleMod' --description 'My first mod.' --json
& $sdvkit project inspect .\ExampleMod --json
& $sdvkit project build .\ExampleMod --json
& $sdvkit project package .\ExampleMod --json
```

Build/package output stays under the mod project's ignored `.sdvkit/`. The package result identifies the ZIP to distribute. See the [toolkit guide](docs/toolkit.md) for Content Patcher packs and diagnostics, or follow the [CP authoring recipe](docs/cp-authoring.md).

## Test a mod in the isolated lab

Run these commands from the directory whose ignored `.sdvkit/` should own the lab. Use an existing explicit C# mod project as the target:

```powershell
& $sdvkit project smoke .\ExampleMod --topology single --json
```

A passing smoke confirms the staged mod was loaded by SMAPI, completed the bounded game-tick check, and stopped/reset cleanly. It does not test every feature of the mod.

For a functional or visual check, start a persistent review:

```powershell
& $sdvkit project review start .\ExampleMod --json
& $sdvkit project review status --json
# Exercise your mod and inspect its actual behavior.
& $sdvkit project review stop --json
```

See [live reviews](docs/live-review.md) for disposable worlds, companions, screenshots, and local host/farmhand testing, including [two local screens in one process](docs/live-review.md#local-split-screen-review). [Native MCP](docs/mcp.md) connects an agent to an already-running review.

## Choose a workflow

| Need | Entry point | Supported target / scope |
| --- | --- | --- |
| Create, inspect, package | `project` | C# mods and content packs; C# build |
| Automated smoke | `project smoke` | Standalone C# mod; single or local host/farmhand |
| Functional/visual review | `project review` | C# in either topology; content-pack target in single |
| Inspect game content, inventories, and current world; transfer an exact supported chest quantity | `project review data/map/texture/audio/mod-assets/inventory/container/container-transfer/world` | Data/inventory/container/world support exact owned roles and local screens; other asset families remain single-only, and transfer additionally requires an owned disposable unbound single world and explicit MCP opt-in |
| Agent observation/actions | `project review mcp serve` | Single or one fixed host/farmhand role; actions opt in |

The [capability matrix](docs/README.md#capability-matrix) lists fixture and role requirements.

## Isolation and evidence

Normal saves and normal or mod-manager-owned `Mods` stay outside automatic operations. Builds, lab profiles, disposable saves, logs, and screenshots belong below `.sdvkit/`. SDVKit reuses the installed game and SMAPI's `--mods-path`; Mod Organizer 2 is not required.

This is process/data isolation, not a Windows sandbox for arbitrary mod code. Build success, SMAPI load, functional behavior, and visual acceptance are separate results. SDVKit does not automatically download missing mods or providers.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for source builds and checks, and the [release procedure](docs/releasing.md) for artifact verification and focused live acceptance.

Repository agent workflows: [project smoke](.agents/skills/sdv-project-smoke/SKILL.md) and [project review](.agents/skills/sdv-project-review/SKILL.md).

## Credits and license

Inspired by [skyrimvr-claude-toolkit](https://github.com/WingedGuardian/skyrimvr-claude-toolkit); the live-lab design also studies [StardewValley-MCP](https://github.com/luy-0/StardewValley-MCP) and [stardew-valley-ai-modkit](https://github.com/liminalwarmth/stardew-valley-ai-modkit).

[MIT](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)

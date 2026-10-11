---
name: sdv-build-package
description: Check, build and package a Stardew Valley mod with SDVKit into a release ZIP, and report what was verified. Use when the user wants to build a mod, prepare a release, or fix check, build or packaging errors.
---

# Check, build and package

1. Check first. It validates the manifest, `content.json` and the files they reference, without starting the game:

   ```powershell
   .\sdvkit.cmd project check .\workspaces\MyMod --json
   ```

   Fix every reported problem in the mod's source before building.
2. Build a C# mod against the installed game and SMAPI:

   ```powershell
   .\sdvkit.cmd project build .\workspaces\MyMod --json
   ```

   With several `.csproj` files, add `--project <relative.csproj>`. With several game installations, `doctor` reports `ambiguous`; ask the user which one to use and pass `--game-path`.
3. Package:

   ```powershell
   .\sdvkit.cmd project package .\workspaces\MyMod --json
   ```

   The ZIP lands in the mod's own `.sdvkit/`. Report its path and hash from the JSON.
4. Say clearly what this proves: the mod builds and packages. It does not prove that SMAPI loads it or that it works in the game. For that, use `sdv-project-smoke` or `sdv-live-review`.

Never copy the build into the game's `Mods` folder; the live lab stages it by itself.

Reference: {{docs}}/toolkit.md

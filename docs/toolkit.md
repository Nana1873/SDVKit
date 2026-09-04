# Modding toolkit

Create and package a mod without deploying it into normal Mods. Complete [installation](../README.md#install) first; examples use its `$sdvkit` variable.

Success is an empty `problems` array and exit `0`; a package result identifies the archive below the selected project's `.sdvkit/packages`. Building code requires `doctor` to report exactly one ready installation. Creating and inspecting projects do not require a game.

## Inspect an installation or project

```powershell
& $sdvkit doctor --json
& $sdvkit project inspect [path] --json
```

`doctor` checks the supported Windows custom-targets, Steam (including additional libraries), GOG, and Xbox locations. An installation is ready only when the Stardew Valley and SMAPI executables and assemblies are present together. Its versioned JSON reports `ready`, `ambiguous`, or `notFound` and lists only complete installations.

`project inspect` reads the selected directory, or the current directory when no path is given. It classifies manifests as `smapiMod` through `EntryDll`, or `contentPack` through `ContentPackFor.UniqueID`; a tree containing separate manifests of both kinds is `hybrid`. Required identity fields and the classification fields are checked, but this is not a replacement for SMAPI's complete manifest schema validation. Project and manifest paths within the result are relative and sorted. `bin`, `obj`, `.git`, and `.sdvkit` directories are ignored, and child directory links are not followed.

Both commands are read-only. They do not create `.sdvkit`, inspect saves or a normal `Mods` directory, deploy files, or launch the game. Exit code `0` means a ready or recognized result, `2` is a CLI usage error, and `3` is a controlled discovery or inspection outcome such as not found, ambiguous, or invalid.

## Create, build, and package

Create a buildable SMAPI C# mod or a minimal Content Patcher pack:

```powershell
& $sdvkit project create smapi-mod .\ExampleMod --name "Example Mod" --author Nana --unique-id Nana.ExampleMod --description "A minimal SMAPI mod." --json
& $sdvkit project create content-pack .\ExamplePack --name "Example Pack" --author Nana --unique-id Nana.ExamplePack --description "A minimal Content Patcher pack." --json
```

The SMAPI project contains only `.gitignore`, `<mod>.csproj`, `ModEntry.cs`, and `manifest.json`. It targets .NET 6 and references `Pathoschild.Stardew.ModBuildConfig` 4.4.0. The content pack contains only `.gitignore`, `manifest.json`, and a no-op `content.json` using Content Patcher format 2.9.0. Creation accepts a missing or empty destination and never overwrites existing content.

Build and package an inspected project:

```powershell
& $sdvkit project build .\ExampleMod --json
& $sdvkit project package .\ExampleMod --json
& $sdvkit project package .\ExamplePack --json
```

`project build` requires exactly one classified SMAPI code manifest, one C# project, and one ready Stardew Valley + SMAPI installation from `doctor`. It runs the normal `dotnet build` and official ModBuildConfig path in `Release`, while forcing `EnableModDeploy=false` and keeping build output and logs below the project's ignored `.sdvkit/` directory.

For C# mods, `project package` lets ModBuildConfig select the declared release output. For Content Patcher packs, it archives the selected manifest root while excluding source projects, build and repository state, game binaries, executables, and XNB files; a save marker or `Saves` directory rejects the package instead of producing a partial archive. Release ZIPs are written below `.sdvkit/packages` and validated to contain one relative top-level mod directory without traversal paths. Neither command writes to a normal or mod-manager-owned `Mods` directory, reads save contents, or launches the game.

All toolkit JSON uses relative paths for project-owned files and archives. Exit code `0` means success, `2` is a CLI usage error, and `3` is a controlled create, build, or package outcome; build diagnostics are kept in the reported `.sdvkit/logs` file.

## Next: test in game

Use [project smoke](live-review.md#automated-smoke) for a standalone C# mod, or [interactive review](live-review.md#start-a-review) for functional checks and content packs. Build and packaging success alone do not prove game behavior.

## Common problems

| Result | Next step |
| --- | --- |
| `doctor` reports `notFound` or `ambiguous` | Check the supported installation/SMAPI setup; do not guess an installation or treat discovery failure as a build failure. |
| Project is `hybrid` or ambiguous | Select the actual supported mod root; do not automatically reshape the project. |
| C# build fails | Open the build log named by the result below the project's `.sdvkit/`. |
| A content pack needs a provider | Select the installed provider explicitly for review; SDVKit does not fetch dependencies. |

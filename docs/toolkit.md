# Modding toolkit

Create and package a mod without deploying it into normal Mods. Complete [installation](../README.md#install) first; examples use its `$sdvkit` variable.

Success is an empty `problems` array and exit `0`; a package result identifies the archive below the selected project's `.sdvkit/packages`. Building code requires one complete game/SMAPI installation, selected explicitly or discovered uniquely. Creating, inspecting, and checking authoring files do not require a game.

## Inspect an installation or project

```powershell
& $sdvkit doctor --json
& $sdvkit project inspect [path] --json
```

`doctor` checks the supported Windows custom-targets, Steam (including additional libraries), GOG, and Xbox locations. An installation is ready only when the Stardew Valley and SMAPI executables and assemblies are present together. Its versioned JSON reports `ready`, `ambiguous`, or `notFound` and lists only complete installations in `installations`. When supported candidate directories are incomplete, additive `incompleteCandidates` entries name their `missingRequirements` (filenames) and concrete `actions`. They never count toward readiness or ambiguity. Omitted-selection status and exit codes keep their existing meaning. Use `doctor --game-path <directory> --json` to validate one explicit installation, including a path outside automatic discovery; missing game files require store repair/installation, while missing SMAPI files require installing/repairing SMAPI in that same directory.

`project inspect` reads the selected directory, or the current directory when no path is given. It classifies manifests as `smapiMod` through `EntryDll`, or `contentPack` through `ContentPackFor.UniqueID`; a tree containing separate manifests of both kinds is `hybrid`. Required identity fields and the classification fields are checked, but this is not a replacement for SMAPI's complete manifest schema validation. Project and manifest paths within the result are relative and sorted. `bin`, `obj`, `.git`, and `.sdvkit` directories are ignored, and child directory links are not followed.

Both commands are read-only. They do not create `.sdvkit`, inspect saves or a normal `Mods` directory, deploy files, or launch the game. Exit code `0` means a ready or recognized result, `2` is a CLI usage error, and `3` is a controlled discovery or inspection outcome such as not found, ambiguous, or invalid.

## Check authoring files offline

```powershell
& $sdvkit project check .\ExamplePack
& $sdvkit project check .\ExamplePack --json
```

Select **one mod root containing `manifest.json`**; omit the path to use the current directory. The check is read-only and needs no game, network, build, or active review. It uses the [official SMAPI schemas](https://github.com/Pathoschild/SMAPI/blob/79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0/docs/technical/web.md#using-a-schema-file-directly), distributed with SDVKit at that exact commit. The [bundled schema notes](../src/SdvKit.Cli/Projects/Schemas/README.md) document hashes, LGPL source distribution, Draft 7/.NET regex compatibility, and the manual update policy. Content Patcher support is **Format 2.9.x**, matching generated 2.9.0 packs.

| Selected file | Rule |
| --- | --- |
| `manifest.json` | Required for a C# mod or Content Patcher pack; official manifest schema, including `%ProjectVersion%`. No built DLL is required. |
| `content.json` | Required when `ContentPackFor.UniqueID` is `Pathoschild.ContentPatcher`; official CP schema. Other providers report `unsupportedProvider`; their manifest and i18n are still checked. |
| Direct `i18n/*.json` | Checked when `i18n` exists; that directory requires `default.json`. Values must be strings. No translation completeness or locale coverage claim. |

Comments and trailing commas are accepted. Duplicate JSON properties are errors. A `$schema` declaration is optional; if present it must exactly match the corresponding official `https://smapi.io/schemas/manifest.json`, `content-patcher.json`, or `i18n.json` URL. Unknown/mismatched declarations report `unsupportedSchema`, and other CP format strings report `unsupportedFormat`; no schema is fetched from the project or network.

There is no recursive mod discovery: nested packs, CP Include fragments, configuration files, assets, and nested i18n directories are outside this check. `Include`, `FromFile`, token-generated paths, and authoring `$ref` values are never followed, even when they look static. File-existence checks cover only the required files above. Root/ancestor junctions, selected file links, and an i18n directory link are rejected. Source files are never repaired or rewritten.

Exit `0` means the supported files passed this schema snapshot, `2` means invalid CLI arguments, and `3` means a file, schema, or unsupported-scope problem. JSON reports `status`, `schemaSource` (the upstream commit), `files` (files evaluated against their schema), and `problems` with a relative `file`, `field`, `code`, and English `message`. Schema fields use JSON Pointer, such as `/Changes/0/FromFile` or `/hello`; an empty pointer means the document root. Malformed JSON reports its parser path and one-based line/byte position in the message. Required-property messages name the absent field at its containing object.

Schema validity does not prove asset existence, dependencies, token/condition evaluation, or that a patch applies in game. Use the [live review workflow](live-review.md) for those observations. Packaging does not implicitly run this check.

### Small runnable CP example

From a development workspace, create a fresh pack below its ignored `.sdvkit/`:

```powershell
$pack = Join-Path $PWD '.sdvkit\CheckedGreeting'
& $sdvkit project create content-pack $pack --name 'Checked Greeting' --author SDVKit --unique-id SDVKit.CheckedGreeting --description 'A small CP authoring example.' --json
@'
{
  "Format": "2.9.0",
  "Changes": [
    {
      "Action": "EditData",
      "Target": "Characters/Dialogue/Abigail",
      "Entries": { "Mon": "{{i18n:greeting}}" },
      "When": { "Season": "spring" },
    },
  ],
}
'@ | Set-Content -LiteralPath (Join-Path $pack 'content.json') -Encoding utf8
New-Item -ItemType Directory -Path (Join-Path $pack 'i18n') | Out-Null
'{ "greeting": "A lovely spring Monday!", }' | Set-Content -LiteralPath (Join-Path $pack 'i18n\default.json') -Encoding utf8
& $sdvkit project check $pack --json
```

Expected: exit `0`, `status: "passed"`, three evaluated files, and no problems. Changing `greeting` to the number `42` produces exit `3` with `i18n/default.json`, `/greeting`, and `schemaViolation`; restore the string and check again. This example proves offline authoring checks only. Continue with the [complete CP authoring recipe](cp-authoring.md) to diagnose a deliberate condition failure, refresh selected JSON, observe final Data records and package the authored result.

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

`project build` defaults to exactly one classified SMAPI code manifest, one C# project, and one complete Stardew Valley + SMAPI installation from `doctor`. It runs the normal `dotnet build` and official ModBuildConfig path in `Release`, while forcing `EnableModDeploy=false` and keeping build output and logs below the project's ignored `.sdvkit/` directory.

For an existing repository with multiple projects or installations, select one target without restructuring it:

```powershell
& $sdvkit project build .\ExistingRepository --project 'src\ChosenMod\ChosenMod.csproj' --game-path $gamePath --json
& $sdvkit project package .\ExistingRepository --project 'src\ChosenMod\ChosenMod.csproj' --game-path $gamePath --json
```

Set `$gamePath` to the intended installation directory validated by doctor. `--project` is a root-relative `.csproj` path; absolute paths, paths outside the chosen root, ignored build/state directories, and links/linked ancestors are rejected. Its code manifest must be colocated with that project. Unselected sibling manifests do not determine its identity. Output and logs remain under the chosen root's `.sdvkit/`; selection never changes the source layout. Omitted selectors retain the unique defaults and return controlled ambiguity when needed. Build/package use the same selected project and manifest, including existing valid hybrid/bundled-pack layouts supported by ModBuildConfig. SDVKit does not orchestrate solution builds or rewrite project references; the selected project still controls its normal .NET references and declared package content. A reference can require another project to compile; selection does not disable declared dependencies.

For C# mods, `project package` lets ModBuildConfig select the declared release output. For Content Patcher packs, it archives the selected manifest root while excluding source projects, build and repository state, game binaries, executables, and XNB files; a save marker or `Saves` directory rejects the package instead of producing a partial archive. Release ZIPs are written below `.sdvkit/packages` and validated to contain one relative top-level mod directory without traversal paths. Neither command writes to a normal or mod-manager-owned `Mods` directory, reads save contents, or launches the game.

All toolkit JSON uses relative paths for project-owned files and archives. Exit code `0` means success, `2` is a CLI usage error, and `3` is a controlled create, build, or package outcome; build diagnostics are kept in the reported `.sdvkit/logs` file.

A packaged Content Patcher target can be edited and [reviewed/refreshed](cp-refresh.md)
from the same source root. Review excludes that target's root `.sdvkit` output
from staging and source identity, while preserving link checks and exact runtime
identities. Other ready sources and nested development directories remain strict.

For a complete event/config implementation, deliberate runtime exception, corrected
world effect and ZIP, follow the [SMAPI authoring recipe](smapi-authoring.md).

## Next: test in game

Use [project smoke](live-review.md#automated-smoke) for a standalone C# mod, or [interactive review](live-review.md#start-a-review) for functional checks and content packs. Build and packaging success alone do not prove game behavior.

## Common problems

| Result | Next step |
| --- | --- |
| `doctor` reports `notFound` or `ambiguous` | Read `incompleteCandidates` missing files/actions, or explicitly validate/select one complete directory with `--game-path`. |
| Project is `hybrid` or ambiguous | Build/package support a valid ModBuildConfig hybrid. Use `--project` for one colocated code project/manifest; review still requires a standalone mod artifact. |
| `projectSelectionInvalid` / `projectManifestMismatch` | Select an existing root-relative `.csproj` beside its own code `manifest.json`, outside ignored directories and links. |
| C# build fails | Open the build log named by the result below the project's `.sdvkit/`. |
| A content pack needs a provider | Select the installed provider explicitly for review; SDVKit does not fetch dependencies. |

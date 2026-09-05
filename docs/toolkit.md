# Modding toolkit

Create and package a mod without deploying it into normal Mods. Complete [installation](../README.md#install) first; examples use its `$sdvkit` variable.

Success is an empty `problems` array and exit `0`; a package result identifies the archive below the selected project's `.sdvkit/packages`. Building code requires `doctor` to report exactly one ready installation. Creating, inspecting, and checking authoring files do not require a game.

## Choose a mod workspace

For new mods sharing one lab, we recommend a `workspaces/<ModName>/` source folder alongside the lab's `.sdvkit/`. This is an optional directory convention: `project create` still requires an explicit destination, and existing projects or user-selected paths can stay where they are. The lab root is a user-chosen directory and does not require an SDVKit source checkout.

```text
StardewDevLab/
|-- workspaces/
|   |-- ExampleMod/
|   |   |-- manifest.json
|   |   |-- ModEntry.cs
|   |   |-- ExampleMod.csproj
|   |   `-- .sdvkit/          # project build/package output
|   `-- ExamplePack/
|       `-- pack/            # clean content-pack root for review
|           |-- manifest.json
|           `-- content.json
`-- .sdvkit/
    `-- lab/                 # shared live state and staged test copies
```

Keep durable mod source, including agent-authored source, in its own workspace; each workspace may have its own Git repository and focused agent session. Git setup is separate from `project create`. Reserve ignored `.sdvkit/` directories for generated output and lab state. The disposable examples below and in the [CP authoring recipe](cp-authoring.md) explicitly choose `.sdvkit/` for their sample projects; that is not a default for a user's lasting mod project.

For a content-pack review, select the clean pack subdirectory (such as `workspaces/ExamplePack/pack/`) with workspace Git metadata outside it. Follow the [CP authoring recipe](cp-authoring.md) for provider selection and review/package order.

Run these commands from the chosen lab root, using `$sdvkit` from [installation](../README.md#install):

```powershell
& $sdvkit project create smapi-mod .\workspaces\ExampleMod --name 'Example Mod' --author 'ExampleAuthor' --unique-id 'ExampleAuthor.ExampleMod' --description 'My first mod.' --json
& $sdvkit project inspect .\workspaces\ExampleMod --json
```

For live work, keep the current directory at that same lab root and pass the explicit mod path, for example `& $sdvkit project smoke .\workspaces\ExampleMod --topology single --json`. Run review lifecycle and MCP commands from the same lab root too, even when the agent edits source in a mod workspace. This reuses one lab without creating a complete lab per mod. Ordinary `project build` and `project package` output belongs to the selected mod's `.sdvkit/`; review preparation and runtime state belong to the lab's `.sdvkit/`. Follow the [live review guide](live-review.md) for prerequisites and ownership checks before testing.

## Inspect an installation or project

```powershell
& $sdvkit doctor --json
& $sdvkit project inspect [path] --json
```

`doctor` checks the supported Windows custom-targets, Steam (including additional libraries), GOG, and Xbox locations. An installation is ready only when the Stardew Valley and SMAPI executables and assemblies are present together. Its versioned JSON reports `ready`, `ambiguous`, or `notFound` and lists only complete installations.

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

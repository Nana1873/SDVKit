# Modding toolkit

Create and package a mod without deploying it into your normal `Mods`. Complete [installation](../README.md#install) first; the examples use its `$sdvkit` variable.

Success is an empty `problems` array and exit `0`. A package result names the archive below the project's `.sdvkit/packages`. Building code needs one complete game and SMAPI installation, either selected with `--game-path` or the only one found. Creating, inspecting and checking authoring files need no game.

## Choose a mod workspace

For new mods that share one lab, we recommend a `workspaces/<ModName>/` source folder next to the lab's `.sdvkit/`. This layout is optional. `project create` still needs a destination you name, and existing projects can stay where they are. The [lab root](glossary.md#places) is any folder you choose; it needs no SDVKit source checkout.

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

Keep lasting mod source, including source an agent wrote, in its own workspace. Each workspace may have its own Git repository and its own agent session. `project create` does not set up Git. Use ignored `.sdvkit/` folders only for generated output and lab state. The throwaway examples below and in the [CP authoring recipe](cp-authoring.md) put their sample projects in `.sdvkit/` on purpose; do not do that for a mod you want to keep.

For a content-pack review, select the clean pack subfolder (such as `workspaces/ExamplePack/pack/`), with the workspace's Git metadata outside it. The [CP authoring recipe](cp-authoring.md) covers provider selection and the order of review and packaging.

### Set up the agent layer

`sdvkit lab init [<lab-root>] --json` prepares a lab root for coding agents. It writes:
- `SDVKIT.md`, the lab guide;
- `KNOWLEDGEBASE.md`, sourced SMAPI 4, Stardew 1.6, Content Patcher and GMCM pitfalls;
- task skills in `.claude/skills/` and `.agents/skills/`;
- a Claude Code hook in `.claude/` that blocks writes to the game folder (including its `Mods`), the normal `Saves` folder, `*.xnb` files and game binaries;
- a `sdvkit.cmd` launcher and an MCP template in `.sdvkit/agent/`.

It creates `CLAUDE.md`, `AGENTS.md`, `.claude/settings.json`, `.gitignore` and `global.json` only when they are missing, and leaves them alone afterwards. Run it again after you install a new SDVKit version. It updates files it wrote that you did not edit, and keeps files you edited unless you pass `--force`. The hook is a safety net, not a sandbox.

Run these commands from the lab root:

```powershell
& $sdvkit project create smapi-mod .\workspaces\ExampleMod --name 'Example Mod' --author 'ExampleAuthor' --unique-id 'ExampleAuthor.ExampleMod' --description 'My first mod.' --json
& $sdvkit project inspect .\workspaces\ExampleMod --json
```

For live work, stay in the same lab root and pass the mod path, for example `& $sdvkit project smoke .\workspaces\ExampleMod --topology single --json`. Run review and MCP commands from the lab root too, even when the agent edits source in a mod workspace. That way all mods share one lab. `project build` and `project package` output goes to the project's own `.sdvkit/`. Review preparation and runtime state go to the lab's `.sdvkit/`. Read the [live review guide](live-review.md) before testing.

## Inspect an installation or project

```powershell
& $sdvkit doctor --json
& $sdvkit project inspect [path] --json
```

`doctor` checks the supported Windows locations: custom targets, Steam (including extra libraries), GOG and Xbox. An installation is ready only when the Stardew Valley and SMAPI executables and assemblies are all present. The JSON reports `ready`, `ambiguous` or `notFound`, and `installations` lists only complete installations. Incomplete candidate folders appear in `incompleteCandidates`, with `missingRequirements` (file names) and `actions`. They do not count toward readiness or ambiguity. To check one installation, including one outside automatic discovery, use `doctor --game-path <directory> --json`. Missing game files need a store repair or install. Missing SMAPI files need SMAPI installed or repaired in that same folder.

`project inspect` reads the given folder, or the current folder without a path. It classifies a manifest as `smapiMod` through `EntryDll`, or as `contentPack` through `ContentPackFor.UniqueID`. A tree with separate manifests of both kinds is `hybrid`. It checks the required identity and classification fields; it does not replace SMAPI's full manifest validation. Paths in the result are relative and sorted. `bin`, `obj`, `.git` and `.sdvkit` folders are skipped, and child folder links are not followed.

Both commands are read-only. They do not create `.sdvkit`, read saves or a normal `Mods` folder, deploy files or launch the game. Exit `0` is a ready or recognized result, `2` a usage error, and `3` a controlled outcome such as not found, ambiguous or invalid.

## Check authoring files offline

```powershell
& $sdvkit project check .\ExamplePack
& $sdvkit project check .\ExamplePack --json
```

Select **one mod root containing `manifest.json`**, or omit the path to use the current folder. The check is read-only. It needs no game, network, build or active review. It uses the [official SMAPI schemas](https://github.com/Pathoschild/SMAPI/blob/79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0/docs/technical/web.md#using-a-schema-file-directly), shipped with SDVKit at that commit. The [bundled schema notes](../src/SdvKit.Cli/Projects/Schemas/README.md) cover hashes, LGPL source distribution, Draft 7/.NET regex compatibility and the manual update policy. Content Patcher support is **Format 2.9.x**, matching generated 2.9.0 packs.

| Selected file | Rule |
| --- | --- |
| `manifest.json` | Required for a C# mod or Content Patcher pack; official manifest schema, including `%ProjectVersion%`. No built DLL is required. |
| `content.json` | Required when `ContentPackFor.UniqueID` is `Pathoschild.ContentPatcher`; official CP schema. Other providers report `unsupportedProvider`; their manifest and i18n are still checked. |
| Reachable literal CP `Include` JSON | Only a `Changes` array is allowed; patches use the same official CP schema under a temporary Format 2.9.0 wrapper. Nested Includes are resolved from the mod root, including comma-delimited paths. Duplicate files are checked once; circular references fail. |
| Literal CP `FromFile` | Checks each unconditional comma-delimited source path for an existing file within the mod root, for every action. Paths are trimmed and empty comma segments ignored; an entirely empty list fails. Asset contents and their dependencies are not parsed. |
| Direct `i18n/*.json` | Checked when `i18n` exists; that directory requires `default.json`. Values must be strings. No translation completeness or locale coverage claim. |

### What the check accepts and skips

- Comments and trailing commas are accepted. Duplicate JSON properties are errors.
- `$schema` is optional. If present, it has to match the official `https://smapi.io/schemas/manifest.json`, `content-patcher.json` or `i18n.json` URL. Other declarations report `unsupportedSchema`. Other CP format strings report `unsupportedFormat`. SDVKit does not fetch a schema from the project or the network.
- The pinned schema checks `FromFile` strings before it resolves files. For example, it rejects a trailing comma inside the path list, although CP ignores that empty segment at runtime.
- References with tokens, or in a patch with `When`, are skipped with a `dynamicReferenceSkipped` or `conditionalReferenceSkipped` warning at the `FromFile` field. Conditional Includes and their children are not read, unless an unconditional literal Include also reaches them. The check never evaluates conditions or tokens, so a pass with warnings is partial coverage.
- Literal paths that leave the mod root or go through a symbolic link or junction fail before the target is read.
- At most 64 JSON files (including `content.json`) and 1 MiB per Include fragment are validated. `includeLimit` and `includeFileTooLarge` warnings name what was skipped; the pack is not rejected, since it may still work in game.
- Not followed: nested packs, config files, unreferenced assets or JSON, nested i18n folders and authoring `$ref` values. Root or parent junctions, selected file links and an i18n folder link are rejected.
- Source files are not repaired or rewritten.

### Read the result

Exit `0` means the checked files passed this schema snapshot. Exit `2` means invalid arguments. Exit `3` means a file, schema or unsupported-scope problem.

The JSON has `status`, `schemaSource` (the upstream commit), `files` (files checked against a schema), `problems` and non-blocking `warnings`. Each diagnostic has a relative `file`, `field`, `code` and English `message`. `field` is a JSON Pointer, such as `/Changes/0/FromFile` or `/hello`; an empty pointer is the document root. A missing or unsafe reference names the source file and its `FromFile` pointer. Errors in included JSON name the fragment itself. Malformed JSON gives the parser path and the one-based line and byte position in the message. A missing required property is reported at its containing object.

The check does not prove asset contents, dependencies, skipped references, token or condition results, or that a patch applies in game. Use the [live review workflow](live-review.md) for that. Packaging does not run this check. CP's [official Include guide](https://github.com/Pathoschild/StardewMods/blob/develop/ContentPatcher/docs/author-guide/action-include.md) describes root-relative paths and the fragment shape.

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

Expected: exit `0`, `status: "passed"`, three checked files and no problems. Change `greeting` to the number `42` and you get exit `3` with `i18n/default.json`, `/greeting` and `schemaViolation`. Restore the string and check again. This example proves offline checks only. Continue with the full [CP authoring recipe](cp-authoring.md) to diagnose a deliberate condition failure, refresh selected JSON, observe final Data records and package the result.

## Create, build, and package

Create a buildable SMAPI C# mod or a minimal Content Patcher pack:

```powershell
& $sdvkit project create smapi-mod .\ExampleMod --name "Example Mod" --author Nana --unique-id Nana.ExampleMod --description "A minimal SMAPI mod." --json
& $sdvkit project create content-pack .\ExamplePack --name "Example Pack" --author Nana --unique-id Nana.ExamplePack --description "A minimal Content Patcher pack." --json
```

The SMAPI project contains only `.gitignore`, `<mod>.csproj`, `ModEntry.cs` and `manifest.json`. It targets .NET 6 and references `Pathoschild.Stardew.ModBuildConfig` 4.4.0. The content pack contains only `.gitignore`, `manifest.json` and a no-op `content.json` in Content Patcher format 2.9.0. The destination must be missing or empty; creation never overwrites existing content.

Build and package an inspected project:

```powershell
& $sdvkit project build .\ExampleMod --json
& $sdvkit project package .\ExampleMod --json
& $sdvkit project package .\ExamplePack --json
```

By default, `project build` needs exactly one classified SMAPI code manifest, one C# project and one complete Stardew Valley + SMAPI installation from `doctor`. It runs the normal `dotnet build` with the official ModBuildConfig in `Release`. It forces `EnableModDeploy=false` and keeps build output and logs below the project's ignored `.sdvkit/`.

In an existing repository with several projects or installations, select one target without restructuring it:

```powershell
& $sdvkit project build .\ExistingRepository --project 'src\ChosenMod\ChosenMod.csproj' --game-path $gamePath --json
& $sdvkit project package .\ExistingRepository --project 'src\ChosenMod\ChosenMod.csproj' --game-path $gamePath --json
```

- Set `$gamePath` to an installation folder that `doctor` validated.
- `--project` is a `.csproj` path relative to the root. Absolute paths, paths outside the root, ignored build or state folders and links (also in parent folders) are rejected.
- The project's code manifest must be in the same folder. Other manifests nearby do not decide its identity.
- Output and logs stay under the root's `.sdvkit/`. Selection does not change the source layout.
- Without selectors, the unique defaults apply, and ambiguity is a controlled result.
- Build and package use the same project and manifest, including valid hybrid or bundled-pack layouts that ModBuildConfig supports.
- SDVKit does not run solution builds or rewrite project references. The project keeps its normal .NET references and package content, so a reference can still require another project to compile.

For C# mods, `project package` lets ModBuildConfig choose the declared release output. For Content Patcher packs, it archives the manifest root and leaves out source projects, build and repository state, game binaries, executables and XNB files. A save marker or `Saves` folder fails the package instead of producing a partial archive. Release ZIPs go below `.sdvkit/packages`. SDVKit checks that each ZIP has one relative top-level mod folder and no traversal paths. Neither command writes to a normal or mod-manager `Mods` folder, reads save contents or launches the game.

Toolkit JSON uses relative paths for project files and archives. Exit `0` is success, `2` a usage error, and `3` a controlled create, build or package outcome. Build diagnostics are in the `.sdvkit/logs` file the result names.

You can edit a packaged Content Patcher target and [review or refresh](cp-refresh.md) it from the same source root. Review leaves that target's root `.sdvkit` output out of staging and source identity, but still checks links and runtime identities. Other ready sources and nested development folders stay strict.

For a full event and config implementation, a deliberate runtime exception, the fixed world effect and the ZIP, follow the [SMAPI authoring recipe](smapi-authoring.md).

## Next: test in game

Use [project smoke](live-review.md#automated-smoke) for a standalone C# mod, or an [interactive review](live-review.md#start-a-review) for functional checks and content packs. Build and package success alone do not prove game behavior.

## Common problems

| Result | Next step |
| --- | --- |
| `doctor` reports `notFound` or `ambiguous` | Read the missing files and actions in `incompleteCandidates`, or validate and select one complete folder with `--game-path`. |
| Project is `hybrid` | Build and package accept a tree with one C# project, its code manifest beside it, and content-pack manifests. This unique target needs no `--project`; ModBuildConfig controls the bundled-pack output. Extract that archive to a fresh ready folder, then [review the bundle](live-review.md#select-a-ready-bundle) in single, with every direct pack selected through `--content-pack`. Source review and smoke still need a standalone target. |
| `projectFileAmbiguous` / `modManifestAmbiguous` | Several C# projects or code manifests need a chosen target. Use `--project` with the root-relative `.csproj` beside its own code `manifest.json`, as in the [selection example](#create-build-and-package). |
| `projectSelectionInvalid` / `projectManifestMismatch` | Select an existing root-relative `.csproj` beside its own code `manifest.json`, outside ignored folders and links. |
| C# build fails | Open the build log that the result names below the project's `.sdvkit/`. |
| A content pack needs a provider | Select the installed provider for the review; SDVKit does not fetch dependencies. |

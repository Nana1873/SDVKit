# Author and verify a Content Patcher pack

Make Stone's description seasonal, explain a deliberate condition failure through
Content Patcher, correct it in the running isolated game, then apply the same
recipe to Wood and package both changes. This verifies final game data, not a
rendered tooltip or dialogue interaction.

## Choose the tool and references

Use **Content Patcher (CP)** for supported content edits such as object fields,
dialogue, images and maps with conditions. Use a **SMAPI C# mod** when the feature
needs code, events or behavior beyond CP's supported actions; start with the
[toolkit](toolkit.md#create-build-and-package) instead. For agents, establish the
desired asset, record, field, condition and expected value before editing. Use
only the relevant references below, matched to the selected versions:

| Decision | Authority |
| --- | --- |
| CP format and canonical asset names | [CP 2.9.1 author guide](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide.md): this example uses `Format: 2.9.0`; asset names omit `Content/`, locale suffix and `.xnb`. |
| Change one field without replacing the object | [EditData / Fields](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/action-editdata.md); inspect the running `Data/Objects` record for its current shape. |
| Conditional application | [Tokens and conditions](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/tokens.md): `Season` and `When`. |
| Offline file validity | [Bundled official schema snapshot and limits](toolkit.md#check-authoring-files-offline). A schema pass does not evaluate runtime conditions. |
| Diagnosis and patch-only reload | [CP troubleshooting](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/troubleshooting.md), [SDVKit diagnosis](cp-diagnosis.md) and [refresh contract](cp-refresh.md). |
| Localize a later user-facing pack | [CP translations](https://github.com/Pathoschild/StardewMods/blob/content-patcher/2.9.1/ContentPatcher/docs/author-guide/translations.md) and the [small i18n example](toolkit.md#small-runnable-cp-example). These two diagnostic descriptions use literal English; adding/changing i18n requires a restart. |

Everything below uses the **CLI**. Existing native MCP [Data tools](mcp.md#canonical-data)
can read the final record; `stardew_mod_diagnostics` can read selected warnings and
exceptions. Creation, checks, packaging, lifecycle, CP diagnosis and refresh use
the CLI here. There is no dedicated CP MCP mutation tool or CP `project smoke`.
For interactive or visual extensions use the existing
[project-review skill](../.agents/skills/sdv-project-review/SKILL.md) and its linked
lab contracts; the project-smoke skill covers standalone C# mods.

## Prerequisites and one lab directory

Use Windows x64, the [required .NET SDK/game/SMAPI](../README.md#requirements), and
an explicit **Content Patcher 2.9.1** ready directory obtained from the
[official CP download](https://www.nexusmods.com/stardewvalley/mods/1915?tab=files).
Choose that version, not an arbitrary latest provider: SDVKit's diagnosis/refresh
currently recognizes exactly 2.9.1. Extract or copy it under your lab's ignored
`.sdvkit/`; leave the download and any normal Mods installation unchanged.

These commands require the authoring capabilities on `main` after PR #128.
The published **v0.7.0 alone does not contain them**. Until a release includes
them, build a fresh checkout using [Contributing](../CONTRIBUTING.md):

```powershell
git clone https://github.com/Nana1873/SDVKit.git SDVKit-authoring
Set-Location SDVKit-authoring
git rev-parse HEAD # retain this commit with your evidence
.\scripts\package-windows-x64.ps1
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
$archive = (Resolve-Path '.sdvkit\distribution\SDVKit-0.7.0-win-x64.zip').Path
$expected = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Hash mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath .sdvkit\authoring-install
$sdvkit = (Resolve-Path '.sdvkit\authoring-install\SDVKit-0.7.0-win-x64\sdvkit.exe').Path
& $sdvkit project check --help
& $sdvkit project review cp-diagnose --help
& $sdvkit project review cp-refresh --help
```

The archive filename follows the repository version; adjust it if a later version
changes that name. Retain the exact ZIP/hash and commit, not just `--version`.
Keep this checkout as the current directory for every lab command below. Place
the selected ready CP directory at `.sdvkit/ContentPatcher`, then set:

```powershell
$lab = $PWD.Path
$pack = Join-Path $lab '.sdvkit\SeasonalObjects'
$provider = (Resolve-Path '.sdvkit\ContentPatcher').Path
$evidence = Join-Path $lab '.sdvkit\authoring-evidence'
New-Item -ItemType Directory -Force $evidence | Out-Null
$providerManifest = Get-Content (Join-Path $provider 'manifest.json') -Raw | ConvertFrom-Json
if ($providerManifest.UniqueID -ne 'Pathoschild.ContentPatcher' -or $providerManifest.Version -ne '2.9.1') {
    throw 'Select the Content Patcher 2.9.1 ready directory.'
}
& $sdvkit doctor --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Require one ready installation. Identify the owner of any existing lab/game and
wait for their verified stop/reset before using this single lab. Follow
[lab preparation](live-review.md#prepare-the-lab); normal Saves/Mods stay outside
the workflow. Record selected CLI provenance, game/SMAPI/provider versions and
protected-path fingerprints before launch when collecting acceptance evidence.
Explicitly select the discovered game's normal `Mods` directory, any separately
used mod-manager Mods directory, and `$env:APPDATA/StardewValley` (including normal
Saves/preferences). Enumerate them read-only before/after, without following
links; retain sorted relative path, entry type/attributes, file length,
`LastWriteTimeUtc` and SHA-256 (`Get-FileHash`) in sibling evidence files. Compare
every field with `Compare-Object`; record missing roots and entry/file counts.
Any added, removed or changed entry needs explanation before claiming isolation.

## Create and check the deliberate failure

```powershell
& $sdvkit project create content-pack $pack --name 'Seasonal Objects' --author ExampleAuthor --unique-id ExampleAuthor.SeasonalObjects --description 'Seasonal object descriptions.' --json
@'
{
  "Format": "2.9.0",
  "Changes": [
    {
      "LogName": "Spring stone description",
      "Action": "EditData",
      "Target": "Data/Objects",
      "Fields": { "390": { "Description": "A stone for spring projects." } },
      "When": { "Season": "summer" }
    }
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $pack 'content.json') -Encoding utf8
& $sdvkit project inspect $pack --json
& $sdvkit project check $pack --json
```

Expect a recognized content pack and `status=passed`, no problems, exit `0`.
The intended result is a **spring** Stone description, but the patch deliberately
requires **summer**. This is valid JSON/schema and a runtime condition mismatch,
not a parser exception. Do not proceed after a failed local check.

## Prepare, start and diagnose

With all lab roles stopped, create the disposable baseline if absent:

```powershell
& $sdvkit lab test-save --topology single --json | Tee-Object (Join-Path $evidence 'baseline.json')
& $sdvkit project review start $pack --topology single --companion $provider --test-save --json | Tee-Object (Join-Path $evidence 'start.json')
& $sdvkit project review status --topology single --json | Tee-Object (Join-Path $evidence 'initial-status.json')
```

CP may create or normalize config files on first launch. Before editing patches,
retain the **start result**, including its exact process/launch and
`artifacts[].stagingPath` resolved against `labRoot`. Compare selected source/staged
files using those start-owned paths. Config drift can make status return
`reviewStagingOwnershipDrifted` with no artifacts; do not depend on that failed
status to recover the paths. If config drift occurred, preserve staged config bytes below
`$evidence` **before stop deletes staging**, stop/reset, deliberately adopt those
bytes only in your generated pack/copied provider, and start again. This is setup,
not a refresh attempt; see [config preparation](cp-refresh.md).

After preparation, wait for fresh status: target and provider loaded,
`testSave.state=ready`, `phase=passed`, `identityVerified=true`. Retain its exact
launch ID, PID and process start time. Never stop a process by name. See the
[disposable-world contract](live-review.md#use-the-disposable-world).

```powershell
& $sdvkit project review status --json | Tee-Object (Join-Path $evidence 'ready-before.json')
```

Keep the SMAPI console idle. Capture CP evidence **before** observing the asset:

```powershell
& $sdvkit project review cp-diagnose --pack ExampleAuthor.SeasonalObjects --provider Pathoschild.ContentPatcher --parse '{{Season}}' --json | Tee-Object (Join-Path $evidence 'before-diagnosis.json')
& $sdvkit project review data get Data/Objects 390 --json | Tee-Object (Join-Path $evidence 'before-stone.json')
```

Expect `state=ready`, CP's season parse `spring`, and the named patch loaded/enabled
with `conditionsMatch=false` and `applied=false`; retain CP's own explanation.
Stone's observed `Description` must differ from the intended literal. If the
world is not spring, investigate fixture identity instead of assuming this failure.
Data inspection may load an asset and change later `applied` state.
For errors use [selected diagnostics and the exact owned log](live-review.md#diagnose-selected-mod-warnings-and-exceptions).
An empty pack-filtered diagnostic result does not mean an error-free whole game.

## Correct, refresh and observe

Change only the existing patch JSON:

```powershell
$contentPath = Join-Path $pack 'content.json'
$content = Get-Content -LiteralPath $contentPath -Raw | ConvertFrom-Json
$content.Changes[0].When.Season = 'spring'
$content | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $contentPath -Encoding utf8
& $sdvkit project check $pack --json
& $sdvkit project review cp-refresh $pack --pack ExampleAuthor.SeasonalObjects --provider Pathoschild.ContentPatcher --file content.json --observe-data Data/Objects --key 390 --json | Tee-Object (Join-Path $evidence 'stone-refresh.json')
& $sdvkit project review status --json | Tee-Object (Join-Path $evidence 'after-stone-status.json')
& $sdvkit project review data get Data/Objects 390 --json | Tee-Object (Join-Path $evidence 'after-stone.json')
```

Require refresh `state=observed`, an acknowledged reload and correlated diagnosis,
then compare the returned record: `Description` must equal
`A stone for spring projects.` Compare the exact launch ID, PID and process start
time with pre-refresh status; all must remain identical. A written command or
`observed` result without comparing the value is insufficient.

Refresh supports selected existing patch JSON only. Manifest, config, i18n,
assets, provider, code, new/deleted files and non-patch definitions require
stop/reset and restart (build code when needed). For an uncertain/partial refresh,
retain its receipt/status and follow [exact recovery](cp-refresh.md#identity-and-interrupted-operations);
never blindly replay reload. Keep failed/inconclusive attempts separate.

## Repeat for Wood

This second authored change adds another patch to the same existing `Changes`
array, using Wood's canonical key `388`. It needs no new lifecycle or command:

```powershell
$content = Get-Content -LiteralPath $contentPath -Raw | ConvertFrom-Json
$content.Changes += [pscustomobject]@{
    LogName = 'Spring wood description'
    Action = 'EditData'
    Target = 'Data/Objects'
    Fields = @{ '388' = @{ Description = 'Wood for a fresh spring start.' } }
    When = @{ Season = 'spring' }
}
$content | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $contentPath -Encoding utf8
& $sdvkit project check $pack --json
& $sdvkit project review cp-refresh $pack --pack ExampleAuthor.SeasonalObjects --provider Pathoschild.ContentPatcher --file content.json --observe-data Data/Objects --key 388 --json | Tee-Object (Join-Path $evidence 'wood-refresh.json')
& $sdvkit project review data get Data/Objects 390 --json | Tee-Object (Join-Path $evidence 'stone-after-wood.json')
& $sdvkit project review status --json | Tee-Object (Join-Path $evidence 'after-wood-status.json')
```

Require Wood `Description=Wood for a fresh spring start.`, Stone still corrected,
and the same exact process/launch. You can package at any point and continue
editing, refreshing, or starting another review from this same source pack.
Its root `.sdvkit` output stays outside the staged pack; no clean source copy is needed.

## Finish and package

Preserve the owned SMAPI log and final status before cleanup. Follow
[finish/reset](live-review.md#finish-or-test-persistence):

```powershell
& $sdvkit project review stop --topology single --json | Tee-Object (Join-Path $evidence 'stop.json')
& $sdvkit project review reset --topology single --json | Tee-Object (Join-Path $evidence 'reset.json')
& $sdvkit project review status --topology single --json | Tee-Object (Join-Path $evidence 'final-status.json')
```

Verify exact process exit, removed owned staging/mount/mailbox, restored fixture,
and unchanged protected normal paths. An isolated-option restoration warning is
separate from blocked cleanup. Keep evidence below `$evidence`, including a small
table of create/check, baseline/config setup, diagnosis, each edit-to-observation,
packaging and cleanup elapsed times, manual interventions, and failed/inconclusive
attempts. Report the expected and actual values plus ZIP identity, not just a
successful command count. No normal save or mod deployment is part of this recipe.

After verified cleanup, produce the final authored ZIP:

```powershell
& $sdvkit project package $pack --json | Tee-Object (Join-Path $evidence 'package.json')
```

Require exit `0` and a ZIP below `$pack/.sdvkit/packages`. Compare the returned
`entries` with the small authored file set: `manifest.json`, `content.json`, and
`config.json` only if CP generated one during setup, all beneath one mod directory.
Keep reports and backups outside the source pack: arbitrary JSON/Markdown/logs
are not automatically excluded. Retain the ZIP SHA-256. Packaging success is
separate from the preceding live proof.

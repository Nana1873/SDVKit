# Author and review an explicit code and content-pack bundle

**Bundle Arrival** is an original single-player example: its C# code adds seven
gold after loading the isolated fixture, while its own content pack selects Farm
tile (64, 15). Observe money and location separately. Loading both manifest IDs
alone does not establish either effect.

The [source example](examples/bundle-arrival/ModEntry.cs) uses SMAPI's supported
`SaveLoaded` event, `IContentPack.ReadJsonFile`, and `IContentPackHelper.GetOwned`.
Its project uses the official ModBuildConfig 4.4.0 `ContentPacks` item, described
in the [SMAPI packaging guide](https://github.com/Pathoschild/SMAPI/blob/4.5.2/docs/technical/mod-package.md#bundled-content-packs).
That build creates one outer archive folder containing a code-mod directory and
its pack directory. SDVKit reuses that package without a new bundle format.

## Build and freeze the ready selection

Follow [installation](../README.md#install), select one complete installation
with `doctor --game-path`, and retain the tested SDVKit commit, ZIP and SHA-256.
Keep the current directory at one [lab root](toolkit.md#choose-a-mod-workspace).
Copy the example's files to an ignored disposable source directory below that
lab's `.sdvkit/`; do not edit a normal Mods directory. Set `$source` to that copy
and `$gamePath` to the selected installation:

```powershell
& $sdvkit project inspect $source --json
& $sdvkit project check $source --json
& $sdvkit project build $source --game-path $gamePath --json
$package = & $sdvkit project package $source --game-path $gamePath --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $package.problems.Count -ne 0) { throw 'Package failed.' }
$archive = Join-Path $source $package.archive
$ready = Join-Path $PWD '.sdvkit\bundle-arrival-ready'
if (Test-Path $ready) { throw 'Choose a fresh extraction directory.' }
Expand-Archive -LiteralPath $archive -DestinationPath $ready
Get-FileHash -Algorithm SHA256 $archive
Get-ChildItem -LiteralPath $ready
```

Inspect the reported archive entries and extracted members. Set `$bundle` to
the archive's one outer folder and `$pack` to its direct `ArrivalPack` child.
The outer name comes from ModBuildConfig; use the reported layout rather than
guessing it. `project inspect $bundle --json` must report exactly code ID
`SDVKit.BundleArrival` and pack ID `SDVKit.BundleArrival.Destination`, both 1.0.0.
The root code check covers only its root manifest. A separate pack check reports
`unsupportedProvider` for this custom provider; that is a checker coverage limit,
not proof that the provider's `arrival.json` was validated. The example validates
its bounded destination in C# after loading.

## Review the exact bundle and observe both effects

Identify the lab owner and actual executable/build before launching; reuse an
existing valid [single fixture baseline](live-review.md#use-the-disposable-world)
when available. Retain its baseline money from the isolated fixture save, or run
an owned standalone code-only control if an independent comparison is needed.
Do not regenerate a fixture owned by another task.

```powershell
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
& $sdvkit project review start $bundle --content-pack $pack --game-path $gamePath --topology single --test-save --json
& $sdvkit project review status --topology single --json
& $sdvkit project review diagnostics --mod SDVKit.BundleArrival --topology single --json
& $sdvkit project review diagnostics --mod SDVKit.BundleArrival.Destination --topology single --json
```

Wait for the exact owned review to pass its fixture/load gate. Retain the target
and pack roles, source selections, staged file identities and loaded IDs/versions.
The target's `sourceRoot` is the selected bundle root; its `stagingPath` names
only the code member. The pack's `sourceRoot` names its explicitly selected child.
Code files and pack files have separate hashes; neither copy contains the other.
Status's local-player snapshot must observe baseline money plus seven and Farm
at tile (64, 15). No physical or simulated input is needed for these effects.

Stop and start again with the same outer root, pack selection, topology, fixture
and installation. Verify fresh process/launch identity, the same staged bytes,
both loaded IDs and both effects. This example does not save, so each restart
loads the retained unchanged fixture and adds seven once to its loaded money;
it does not prove saved progression.

```powershell
& $sdvkit project review stop --topology single --json
& $sdvkit project review start $bundle --content-pack $pack --game-path $gamePath --topology single --test-save --json
& $sdvkit project review status --topology single --json
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
```

Require exact process exit, staging removal and fixture reset. Keep archive,
baseline comparison, observations, logs and cleanup results under ignored
`.sdvkit/`. Record build, offline tests, live load, separate functional effects,
restart and teardown at their actual evidence levels.

## Selection contract and limits

Direct bundle review supports **ready directories in topology single**. Select
either an outer container with exactly one direct code-mod child, or a root code
mod with direct pack children. Explicitly pass **every** embedded pack directory
through repeatable `--content-pack`; no packs or providers are selected silently.
External companions/packs retain their existing behavior. Required providers and
minimum versions must exist in this exact explicit set, including the target if
it is the provider. Duplicate IDs and staging names fail before launch.

Every manifest must be accounted for by the one code member or a selected direct
pack member. Additional/nested code mods, unselected manifests, missing members
or DLLs, malformed/duplicate manifest properties, links, source/build/save files,
and unsafe paths fail before launch. Existing ready-file safety applies to the
whole container and each copied member. Container metadata outside member roots
is checked for safety but is not staged. A retained active bundle start requires
the exact same outer root, selected pack/companion paths and project selection;
changing the member set requires the normal owned stop/reset sequence.

Source bundle review and network-2 bundle review remain unsupported. Use the
existing build/package followed by a fresh ready extraction. Standalone source,
ready code-mod, and explicit content-pack target workflows keep their existing
contracts; `project smoke` still requires a standalone code mod.

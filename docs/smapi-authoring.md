# Author and diagnose a SMAPI feature

Build **Morning Arrival**, a small C# mod which starts each day at a configured
Farm tile. Diagnose a deliberate event-handler exception, fix and rebuild, then
observe the farmer's actual location and tile before packaging. This proves a
single-player world effect, not just mod loading or a log message.

## Choose the tool and references

Prefer [Content Patcher](cp-authoring.md) for supported conditional asset edits.
Choose SMAPI C# for event-driven game behavior like this local farmer warp.
Use these focused primary references, checked against **SMAPI 4.5.2 / Stardew
Valley 1.6.15**; the game API can change independently of SMAPI:

| Decision | Authority and application |
| --- | --- |
| Entry and lifecycle | [Mod structure](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Mod_structure): initialize config and subscribe once in Entry; do not access a loaded farmer there. |
| Select an event | [SMAPI 4.5.2 game-loop events](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/Events/IGameLoopEvents.cs): DayStarted runs after day initialization, including loading a save. GameLaunched is for post-Entry initialization; SaveLoaded is for loading an existing save. |
| Typed configuration | [Config guide](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Config) and [4.5.2 IModHelper](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/IModHelper.cs): public properties and defaults; ReadConfig creates a missing config.json. This recipe reads once at startup, so edits require a restart. |
| World and player responsibility | [SMAPI 4.5.2 Context](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/Context.cs): guard world readiness. This example explicitly skips multiplayer; a future shared-world feature must decide host authority separately from local-player behavior and test both roles. |
| Game operation | Compile against the selected installation with [ModBuildConfig](https://github.com/Pathoschild/SMAPI/blob/4.5.2/docs/technical/mod-package.md). The example uses the game's public Game1.warpFarmer(location, x, y, flip) overload, also used by SDVKit's existing fixture navigation. |
| Diagnose a failure | [SMAPI troubleshooting](https://stardewvalleywiki.com/Modding:Modder_Guide/Test_and_Troubleshoot) and [owned log diagnostics](live-review.md#diagnose-selected-mod-warnings-and-exceptions): retain actual exception text, handler and attribution limits before editing. |

Authoring/build/package and review lifecycle use CLI. Observation and diagnosis
can use existing [native MCP](mcp.md); no input or fixture-action opt-in is needed.
The [review skill](../.agents/skills/sdv-project-review/SKILL.md) covers ownership
and acceptance without duplicating this authoring recipe.

## Select the installation and project

Use Windows and the [required SDK/game/SMAPI](../README.md#requirements).
These commands need `main` after PR #133; **published v0.7.0 alone lacks the
selection and diagnosis workflow**. Build/package a fresh checkout using the
[CP recipe's CLI setup](cp-authoring.md#prerequisites-and-one-lab-directory),
omitting its CP provider and CP-specific help commands. Retain the exact commit,
SDVKit ZIP and SHA-256; set `$sdvkit` to its extracted absolute executable path.

Keep one current directory as the lab owner throughout:

```powershell
$lab = $PWD.Path
$mod = Join-Path $lab '.sdvkit\MorningArrival'
$evidence = Join-Path $lab '.sdvkit\smapi-authoring-evidence'
New-Item -ItemType Directory -Force $evidence | Out-Null
& $sdvkit doctor --json
# Set $gamePath to the intended complete installation returned by doctor.
& $sdvkit doctor --game-path $gamePath --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
& $sdvkit project create smapi-mod $mod --name 'Morning Arrival' --author ExampleAuthor --unique-id ExampleAuthor.MorningArrival --description 'Start the day at a configured Farm tile.' --json
& $sdvkit project inspect $mod --json
$project = 'MorningArrival.csproj'
```

Verify the generated project filename in inspect; use that relative path for
`$project`. For an existing standalone C# mod, explicitly select its root and
colocated project/manifest instead; do not run create over existing files. Follow
[selection](toolkit.md#create-build-and-package) for multiple projects. The
snippet below replaces the generated ModEntry.cs, so adapt an existing mod's
handler rather than overwriting unrelated source.

## Implement and build the deliberate failure

Write the following original source to `$mod/ModEntry.cs`:

```csharp
using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace MorningArrival;

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;
    public int TileX { get; set; } = 64;
    public int TileY { get; set; } = 15;
}

public sealed class ModEntry : Mod
{
    private ModConfig Config = new();

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.DayStarted += OnDayStarted;
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer || !Config.Enabled)
            return;

        // Deliberate diagnosis exercise: remove this line after capturing the error.
        if (Config.TileX >= 0)
            throw new InvalidOperationException("Morning Arrival deliberate fault before Farm warp.");
        Game1.warpFarmer("Farm", Config.TileX, Config.TileY, false);
    }
}
```

The intentionally unreachable warp can produce compiler warning CS0162; it is
not the runtime exception being diagnosed. The feature targets a known clear
Farm tile in the disposable standard-farm baseline. Configuring another tile
requires checking that map's bounds, terrain and occupancy; this is not a general
safe-spawn resolver. Leave normal saves and Mods outside this exercise.

```powershell
& $sdvkit project check $mod --json | Tee-Object (Join-Path $evidence 'check-broken.json')
& $sdvkit project build $mod --project $project --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'build-broken.json')
```

Require exit 0 and retain the build log named by the result. `project check`
validates supported authoring files, **not C# or config semantics**. Compilation
is a separate result and cannot detect the intentional runtime fault. All build
output is isolated; SDVKit forces ModBuildConfig deployment off.

## Review and diagnose the actual exception

Identify the lab owner and wait for any previous owner's verified stop/reset.
Record protected normal paths read-only as described in the
[CP recipe](cp-authoring.md#prerequisites-and-one-lab-directory), including actual
external user/mod-manager differences. Prepare the registered disposable baseline
only if absent, while all roles are stopped:

```powershell
& $sdvkit lab test-save --topology single --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'baseline.json')
& $sdvkit project review start $mod --project $project --game-path $gamePath --topology single --test-save --json | Tee-Object (Join-Path $evidence 'start-broken.json')
& $sdvkit project review status --topology single --json | Tee-Object (Join-Path $evidence 'status-broken.json')
& $sdvkit project review diagnostics --mod ExampleAuthor.MorningArrival --limit 20 --json | Tee-Object (Join-Path $evidence 'diagnostics-broken.json')
```

Require exact target ID/version/build, process/launch, loaded target and verified
fixture (`state=ready`, `phase=passed`, `identityVerified=true`). Bind an existing
MCP client to `sdvkit project review mcp serve --topology single` in this same lab, following
[client configuration](mcp.md#client-configuration). Call:

```json
{"name":"stardew_mod_diagnostics","arguments":{"modId":"ExampleAuthor.MorningArrival","limit":20}}
{"name":"stardew_runtime_get","arguments":{}}
```

Retain the actual returned exception, its `OnDayStarted` stack frame, selected
logger attribution, counts, phase and truncation/withheld-context fields. The
expected message is `Morning Arrival deliberate fault before Farm warp.`. Explain
that the handler threw before calling warp; confirm the broken run's observed
position is different from the configured destination. Attribution identifies a
logger, not universal causality; zero matches is not proof of error-free code.
Do not substitute an Entry message or compiler warning for this runtime evidence.

Preserve full available isolated logs promptly, using the exact status-owned
paths in the [diagnostic contract](live-review.md#diagnose-selected-mod-warnings-and-exceptions).
Keep failed attempts and unknown completion visible. Raw preparation logs can
be removed by ordinary lifecycle cleanup; report unavailable logs honestly.
Then stop and reset using the commands in the final section, retaining separately
named broken-phase results. Never edit or hot-swap an active staged DLL.

## Fix, rebuild and observe the feature

Remove the deliberate `if (Config.TileX >= 0)` / throw and its exercise comment from ModEntry.cs. Keep the
rest unchanged. Re-run `project check` and `project build` with the same selectors,
retaining new results and the corrected source. Start a new `--test-save` review
with the same target/project/game selectors. A C# rebuild requires a new process;
CP refresh does not reload assemblies or this startup-read config.

Verify the new exact launch/build and fixture identities. Call
`stardew_runtime_get` and require a world-ready farmer at **Farm, tile 64,15**;
retain the returned observation and time. Compare with the broken run. Read
`stardew_mod_diagnostics` again and require no matching deliberate fault in the
new launch. A loaded mod or its own success log is insufficient. This checks
arrival on the loaded day's DayStarted event; it does not claim an overnight,
persistence, multiplayer, visual or arbitrary-map acceptance.

Retain the staged generated `config.json` before stop. It must match the typed
defaults (`Enabled=true`, `TileX=64`, `TileY=15`). Config is created in the isolated
staged mod; it is not automatically copied back into the source project. To ship
user-specific config, explicitly select and test it as a separate variant; the
normal package here uses the verified compiled defaults.

## Finish and package

Preserve status and full available owned logs before cleanup, then:

```powershell
& $sdvkit project review stop --topology single --json | Tee-Object (Join-Path $evidence 'stop-fixed.json')
& $sdvkit project review reset --topology single --json | Tee-Object (Join-Path $evidence 'reset-fixed.json')
& $sdvkit project review status --topology single --json | Tee-Object (Join-Path $evidence 'final-status.json')
& $sdvkit project package $mod --project $project --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'package-fixed.json')
```

Require exact process exit, removed owned staging/mount/mailbox and reset fixture.
Compare protected paths and source hashes, separating intended author edits and
external user/Vortex activity; investigate any SDVKit-caused differences. An
isolated-option restoration warning is separate from blocked cleanup.

Inspect the returned ZIP entries and SHA-256. Compare its manifest/DLL bytes with
the retained corrected live staged artifact; packaging rebuilds through normal
ModBuildConfig, so compilation alone is not artifact identity. If compiled bytes
differ, explain the difference and verify the final artifact before claiming the
ZIP carries the observed feature. Keep generated config/logs/evidence out of the
release unless explicitly selected; never include game binaries or saves.

Record a small evidence table with start/end or elapsed time for select/create,
implementation/check/build, baseline preparation, broken diagnosis, fix-to-actual
observation, packaging and cleanup. Include manual interventions, failed or
inconclusive attempts, CLI/archive/mod hashes, source snapshots, runtime and
fixture identities, actual values and log availability. Report measured friction,
not command counts. No normal deployment or SDVKit release is part of this recipe.

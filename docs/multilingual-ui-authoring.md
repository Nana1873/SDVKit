# Multilingual SMAPI dialogue authoring recipe

> Verification status: the source, deliberate offline failure, correction, build,
> package, fresh extraction, exact-package live review, native language transition,
> controller path, save/restart, idempotence, mismatch rejection, restoration, and
> cleanup below were verified from the same frozen sample package. The evidence
> record states the limits of each observation.

This recipe authors one original SMAPI mod, **Blueberry Greeting**. A console
command opens a vanilla question dialogue with Cancel first and Accept second.
Accepting once changes one player `modData` value from absent to `"1"`; accepting
again is intentionally idempotent. English is the default locale, German is the
second locale, and one German key is deliberately absent so SMAPI's runtime
fallback to `default.json` can be observed.

The workflow uses existing SDVKit and SMAPI behavior only. It does not add a
translation engine, custom menu framework, or general UI automation.

## What each gate can prove

| Gate | Proof | Does not prove |
| --- | --- | --- |
| `project check` | Direct i18n JSON validity, locale key comparison, and compatible placeholder names | Selected in-game language, rendered text, or runtime fallback |
| `project build` | The selected C# project compiles against the selected installation | SMAPI load or feature behavior |
| `project package` plus fresh extraction | Exact distributable entries and bytes | The packaged DLL behaves correctly in-game |
| Exact-package live review | Observed UI, input, language, state, and persistence at the tested scope | General compatibility outside that owned single disposable world |

Read [offline i18n authoring check](i18n-authoring.md), [live review](live-review.md),
[menu inspection](menu-inspection.md), and [native MCP](mcp.md) before extending
the example.

## Create the mod

Keep generated work below an ignored project `.sdvkit/` directory. Run live
commands later from the shared lab root, not from a normal `Mods` or `Saves`
directory.

```powershell
$sdvkit = '<absolute sdvkit.exe from the exact accepted distribution>'
$lab = '<absolute shared lab root>'
$mod = Join-Path $lab '.sdvkit\recipes\BlueberryGreeting'
$evidence = Join-Path $lab '.sdvkit\blueberry-greeting-evidence'
$gamePath = '<absolute complete Stardew Valley installation>'
$project = 'BlueberryGreeting.csproj'

New-Item -ItemType Directory -Path $evidence -Force | Out-Null
& $sdvkit project create smapi-mod $mod `
  --name 'Blueberry Greeting' `
  --author 'SDVKit Example' `
  --unique-id 'SDVKit.Example.BlueberryGreeting' `
  --description 'An original multilingual dialogue and persistence recipe.' `
  --json | Tee-Object (Join-Path $evidence 'create.json')
```

Use this project file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>1.0.0</Version>
    <AssemblyName>BlueberryGreeting</AssemblyName>
    <RootNamespace>BlueberryGreeting</RootNamespace>
    <EnableModDeploy>false</EnableModDeploy>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
  </ItemGroup>
</Project>
```

Use this `manifest.json`:

```json
{
  "Name": "Blueberry Greeting",
  "Author": "SDVKit Example",
  "Version": "1.0.0",
  "Description": "An original multilingual dialogue and persistence recipe.",
  "UniqueID": "SDVKit.Example.BlueberryGreeting",
  "EntryDll": "BlueberryGreeting.dll",
  "MinimumApiVersion": "4.5.2"
}
```

Use this `ModEntry.cs`:

```csharp
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace BlueberryGreeting;

internal sealed class ModEntry : Mod
{
    private const string AcceptKey = "blueberry_accept";
    private const string CancelKey = "blueberry_cancel";
    private const string StateKey = "SDVKit.Example.BlueberryGreeting/Accepted";
    private const string Marker = "BLUEBERRY-42";
    private bool _pendingSave;

    public override void Entry(IModHelper helper)
    {
        helper.ConsoleCommands.Add("blueberry-greeting",
            "Open or inspect the original multilingual greeting recipe.", Run);
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.Saved += OnSaved;
    }

    private void Run(string command, string[] arguments)
    {
        if (!Context.IsWorldReady || Game1.player is null || Game1.currentLocation is null)
        {
            Monitor.Log("BLUEBERRY_GREETING " + JsonSerializer.Serialize(new
            {
                state = "worldNotReady",
                locale = Helper.Translation.Locale,
                accepted = (bool?)null,
                pendingSave = (bool?)null,
                marker = Marker,
                fallback = (string?)null,
            }), LogLevel.Warn);
            return;
        }

        switch (arguments.FirstOrDefault()?.ToLowerInvariant())
        {
            case "open":
                OpenDialogue();
                break;
            case "status":
                LogStatus("ready");
                break;
            case "title" when _pendingSave:
                LogStatus("saveRequired");
                Monitor.Log("Save the accepted greeting before requesting the title transition.", LogLevel.Warn);
                break;
            case "title" when Game1.activeClickableMenu is null:
                LogStatus("titleRequested");
                Game1.ExitToTitle();
                break;
            case "title":
                Monitor.Log("Close the active menu before requesting the title transition.", LogLevel.Warn);
                break;
            default:
                Monitor.Log("Usage: blueberry-greeting open|status|title", LogLevel.Info);
                break;
        }
    }

    private void OpenDialogue()
    {
        bool accepted = IsAccepted();
        string fallback = Translate("fallback.default-only");
        string prompt = Translate(accepted ? "dialogue.completed" : "dialogue.prompt", fallback);
        Response[] responses =
        [
            new Response(CancelKey, Translate("choice.cancel")),
            new Response(AcceptKey, Translate("choice.accept")),
        ];
        Game1.currentLocation.createQuestionDialogue(prompt, responses, (_, answer) =>
        {
            bool before = IsAccepted();
            bool changed = answer == AcceptKey && !before;
            if (changed)
            {
                Game1.player.modData[StateKey] = "1";
                _pendingSave = true;
            }
            bool after = IsAccepted();
            Monitor.Log("BLUEBERRY_GREETING " + JsonSerializer.Serialize(new
            {
                state = "answered",
                locale = Helper.Translation.Locale,
                answer,
                before,
                after,
                changed,
            }), LogLevel.Info);
            Game1.drawObjectDialogue(Translate(after ? "result.accepted" : "result.cancelled"));
        });
        LogStatus("dialogueOpened");
    }

    private string Translate(string key, string? fallback = null) => Helper.Translation.Get(key, new
    {
        farmer = Game1.player.Name,
        marker = Marker,
        fallback = fallback ?? "",
    }).ToString();

    private static bool IsAccepted() => Game1.player.modData.TryGetValue(StateKey, out string? value)
        && value == "1";

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e) => _pendingSave = false;

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        _pendingSave = false;
        LogStatus("saveCompleted");
    }

    private void LogStatus(string state) => Monitor.Log("BLUEBERRY_GREETING " + JsonSerializer.Serialize(new
    {
        state,
        locale = Helper.Translation.Locale,
        accepted = IsAccepted(),
        pendingSave = _pendingSave,
        marker = Marker,
        fallback = Translate("fallback.default-only"),
    }), LogLevel.Info);
}
```

Create `i18n/default.json`:

```json
{
  "$schema": "https://smapi.io/schemas/i18n.json",
  "dialogue.prompt": "Hello {{farmer}}! Recipe marker: {{marker}}. {{fallback}}",
  "dialogue.completed": "Welcome back, {{farmer}}. Recipe marker: {{marker}}. {{fallback}}",
  "fallback.default-only": "Default-only marker: BLUEBERRY.",
  "choice.cancel": "Not now",
  "choice.accept": "Remember this greeting",
  "result.cancelled": "Nothing was changed.",
  "result.accepted": "The blueberry greeting is remembered."
}
```

Start `i18n/de.json` with one deliberate placeholder error and one deliberate
missing key. The prompt uses `{{player}}`, while the source supplies `farmer`.
The omitted `fallback.default-only` key is intentional and must remain omitted
after the placeholder fix.

```json
{
  "$schema": "https://smapi.io/schemas/i18n.json",
  "dialogue.prompt": "Hallo {{player}}! Rezeptmarke: {{marker}}. {{fallback}}",
  "dialogue.completed": "Willkommen zurück, {{farmer}}. Rezeptmarke: {{marker}}. {{fallback}}",
  "choice.cancel": "Jetzt nicht",
  "choice.accept": "Diesen Gruß merken",
  "result.cancelled": "Es wurde nichts geändert.",
  "result.accepted": "Der Blaubeergruß wurde gespeichert."
}
```

## Diagnose the deliberate mismatch

```powershell
$broken = & $sdvkit project check $mod --json |
  Tee-Object (Join-Path $evidence 'check-broken.json') |
  ConvertFrom-Json
$brokenExit = $LASTEXITCODE

if ($brokenExit -ne 3) { throw "Expected project check exit 3, got $brokenExit." }
if ($broken.problems.Count -ne 1 -or
    $broken.problems[0].code -ne 'placeholderMismatch' -or
    $broken.problems[0].file -ne 'i18n/de.json' -or
    $broken.problems[0].field -ne '/dialogue.prompt') {
  throw 'The deliberate placeholder diagnostic changed.'
}
if ($broken.warnings.Count -ne 1 -or
    $broken.warnings[0].code -ne 'missingLocaleKey' -or
    $broken.warnings[0].file -ne 'i18n/de.json' -or
    $broken.warnings[0].field -ne '/fallback.default-only') {
  throw 'The intentional fallback warning changed.'
}
```

Expected problem detail:

```text
Placeholder names differ from default.json: default=[fallback, farmer, marker], locale=[fallback, marker, player].
```

This is a static source diagnostic. Do not launch the game with the deliberately
broken locale merely to reconfirm it.

## Fix exactly one token, then build and package

Change only `{{player}}` to `{{farmer}}` in `dialogue.prompt`. Do not add the
German `fallback.default-only` key.

```powershell
$fixed = & $sdvkit project check $mod --json |
  Tee-Object (Join-Path $evidence 'check-fixed.json') |
  ConvertFrom-Json
$fixedExit = $LASTEXITCODE

if ($fixedExit -ne 0 -or $fixed.status -ne 'passed' -or $fixed.problems.Count -ne 0) {
  throw 'Corrected i18n source did not pass.'
}
if ($fixed.warnings.Count -ne 1 -or
    $fixed.warnings[0].code -ne 'missingLocaleKey' -or
    $fixed.warnings[0].field -ne '/fallback.default-only') {
  throw 'Expected exactly the intentional fallback warning.'
}

& $sdvkit project build $mod --project $project --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'build-fixed.json')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$package = & $sdvkit project package $mod --project $project --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'package-fixed.json') |
  ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Package failed.' }
```

Require the package result to list exactly these entries:

```text
BlueberryGreeting/BlueberryGreeting.dll
BlueberryGreeting/i18n/de.json
BlueberryGreeting/i18n/default.json
BlueberryGreeting/manifest.json
```

Extract `$package.archive` to a fresh directory, run `project inspect` and
`project check` against the extracted `BlueberryGreeting` directory, and compare
the extracted manifest and both locale files byte-for-byte with the accepted
source. Record the ZIP, DLL, and source-file SHA-256 values. Never overwrite an
older extraction or repackage after accepting the artifact.

The draft fixture's verified offline artifact currently has these identities:

```text
ZIP SHA-256: f036129ed4d6ee84458f0422272cd06fd6114dce38554c66c52344659259a408
DLL SHA-256: 99d73738ccfa6704886378fd01a38203f1e8e7951b5581c7d9c46e5c69358a4f
```

These hashes identify the draft fixture only. Replace them if the published
source changes, and rerun every affected gate.

## Exact-package live review

The steps below are the reusable acceptance procedure. The accompanying evidence
blocks record the completed run for the frozen ZIP and DLL identities above. Use
only an available, explicitly owned single-player lab slot and retain new evidence
below `$evidence` when the sample changes.

### Start and bind the exact package

Extract the accepted ZIP once to a fresh `$unpacked` directory and select its
`BlueberryGreeting` child as `$ready`. From `$lab`:

```powershell
Set-Location $lab
& $sdvkit project review status --topology single --json |
  Tee-Object (Join-Path $evidence 'preflight-status.json')
& $sdvkit lab test-save --topology single --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'baseline.json')
& $sdvkit project review start $ready --game-path $gamePath --topology single --test-save --json |
  Tee-Object (Join-Path $evidence 'start-package.json')
& $sdvkit project review status --topology single --json |
  Tee-Object (Join-Path $evidence 'status-package.json')
```

Require `$sdvkit` itself to match the accepted distribution identity. Require
exact launch/process ownership, the registered disposable world,
`SDVKit.Example.BlueberryGreeting` version `1.0.0`, the accepted staged build
identity, and SMAPI loaded-mod confirmation. Compare staged `manifest.json` and
DLL SHA-256 with `$ready`. A start result alone is not load or behavior proof.

Connect one real MCP client from `$lab` to:

```powershell
& $sdvkit project review mcp serve --topology single --allow-input --allow-fixture-actions
```

Record the client configuration and tool-list evidence. The acceptance client
must call `stardew_menu_get`, `stardew_screenshot_capture`, and the permitted
input tools itself; CLI-only equivalents do not satisfy that MCP-client gate.

### Capture the initial language, then cancel in English

Before changing language, retain the initial observed language as
`$initialLanguage`; do not assume it merely because this example expects an
English-first run. Record the mod's runtime locale, a viewport screenshot,
and a read-only snapshot plus SHA-256 of the isolated review profile's
`startup_preferences`. Record the observed native language preference from that
snapshot without editing it. These three observations establish the restoration
target; if they disagree, stop and diagnose instead of choosing one.

1. Deliver `blueberry-greeting status` and require an observed mod log with
   `accepted: false`, `pendingSave: false`, marker `BLUEBERRY-42`, and the
   default-only fallback text. SMAPI can represent its default translation
   locale as the empty string even while the native preference and visible UI
   are English; preserve that exact value instead of rewriting it to `en`.
   Continue only when the runtime text, independently observed native title,
   and isolated preference together establish an English-first run. Delivery
   alone is not proof.
2. Capture `stardew_screenshot_capture { "mode": "viewport", "label": "blueberry-initial-world" }`
   and retain the initial isolated `startup_preferences` observation.
3. Deliver `blueberry-greeting open` once.
4. Call `stardew_menu_get {}`. Require a complete supported question-dialogue
   observation whose rendered prompt contains the actual farmer name,
   `BLUEBERRY-42`, and `Default-only marker: BLUEBERRY.`. Require response order
   `blueberry_cancel`, then `blueberry_accept`, with English visible labels.
5. Capture `stardew_screenshot_capture { "mode": "viewport", "label": "blueberry-en-open" }`
   and inspect the returned PNG independently for the same text and usable layout.
6. Cancel exactly once through an ordinary process-local mouse click. Use the
   observed Cancel bounds and fresh `uiRevision`, for example:

   ```text
   stardew_input_click {
     "x": <integer center x from the observed Cancel bounds>,
     "y": <integer center y from the observed Cancel bounds>,
     "button": "MouseLeft",
     "count": 1,
     "uiRevision": "<fresh revision from the same menu>"
   }
   ```

   Send that payload to `stardew_input_click`. Do not reuse example coordinates.
7. Re-read the menu/state and capture `blueberry-en-cancelled`. Require the native
   callback log to report `answer: "blueberry_cancel"`, `before: false`,
   `after: false`, `changed: false`; require the English cancellation result and
   then close it once through an observed supported path. Confirm status remains
   `accepted: false` and `pendingSave: false`.

Verified evidence:

```text
MCP client/tool-list: real stdio client; 45 advertised tools
Initial runtime locale: empty string; native preference and visible title: English
Initial startup_preferences SHA-256: f8ba4a967190106f156d31a4c6f25b97481c612755badcb0dcc658a17cd1ff1f
English menu: complete DialogueBox; Cancel first, Accept second
English open PNG SHA-256: 00be4a7d60ab18254cbdbd9d23a299fa1549eb742fc347bff0f3ac1a6ab92746
Mouse cancel: one revision-bound click inside the observed Cancel bounds
Cancellation callback: before=false, after=false, changed=false
English result: "Nothing was changed."; the first Escape did not close it, so a fresh
observed close-bound click was used once instead of replaying the uncertain input
```

### Change language through the native title workflow

With no active world menu and no pending save, deliver `blueberry-greeting title`.
Require the mod's `titleRequested` log and a fresh viewport screenshot of the
actual title state. From this point, world-bound menu inspection such as
`project review menu` and `stardew_menu_get`, plus fixture actions, is
unavailable. The passed fixture status is intentionally invalidated while no
world is loaded. The documented owned-process title/loading viewport and input
commands remain available for native language selection until the exact fixture
is revalidated.

Before selecting anything, capture the title viewport as
`blueberry-initial-title`, inspect its visible native text, and require it to
match `$initialLanguage` and the isolated preference observation. This is the
initial native-language observation. Stop on disagreement.

Navigate only through Stardew Valley's observed, supported title-menu language
workflow. Capture a fresh viewport image before each action, identify the visible
Language control and German choice from that image, and use only process-local
review input. The verified run used this observed sequence:

```text
Initial native-title screenshot: visibly English
Observed Language control: title speech-bubble control, selected from a fresh viewport
Input used to open Language Selection: virtual cursor plus one MouseLeft press
Language Selection screenshot: native language grid visibly rendered
Observed German control: DEUTSCH, selected from that fresh viewport
Input used to select German: virtual cursor plus one MouseLeft press
Post-selection title screenshot: visibly German (NEU, LADEN, KOOP, VERLASSEN)
Required recreation: native Load menu, exactly one SDVKit row, then exact-fixture reload
```

Do not infer language selection from source files, an input acknowledgement, or
the expected menu implementation. Require visibly German native title text.
Open Stardew's native Load menu and require exactly one visible save: the
registered disposable work save. If another row exists, stop and resolve that
isolated-profile ambiguity through ownership-aware lab preparation; never guess
which row is the fixture and never inspect or select a normal save. Load the one
exact row and require review status to return to `phase: "passed"` with the same
fixture/save IDs and `identityVerified: true` before reconnecting MCP or using a
fixture action. The recovery is single-player-only, is consumed before live
identity verification, and therefore cannot authorize a second attempt after a
wrong or mismatched save. Finally require `blueberry-greeting status` with the
observed German locale. Do not reset the fixture work save during this transition.

The negative boundary was also exercised with one synthetic decoy inside the
isolated profile only. Selecting `SDVKit2_4624841330222774411` produced an exact
`saveId` mismatch, left the review terminally failed with
`identityVerified: false`, and made MCP startup refuse with
`reviewTestSaveNotReady`. Returning through native menus and loading the exact
fixture did not revive that consumed attempt. The decoy was moved back to its
quarantine path with both hashes unchanged.

### German fallback and controller acceptance

1. In the reloaded ready world, deliver `blueberry-greeting open` once.
2. Call `stardew_menu_get {}` and capture `blueberry-de-open`.
3. Require German prompt and choice text, the substituted farmer name and marker,
   plus the visible English default fallback sentence
   `Default-only marker: BLUEBERRY.`. This mixed rendered result, not the missing
   source key by itself, proves runtime fallback.
4. Require Cancel first and Accept second. Use explicit controller input for the
   acceptance path. Send one `DPadDown` through
   `stardew_input_press { "button": "DPadDown" }`, then re-read the menu and
   require the observed focused component to be `blueberry_accept` before acting.
5. Send one `ControllerA` through
   `stardew_input_press { "button": "ControllerA" }`. Do not repeat either input
   when completion is uncertain; inspect current menu and status first.
6. Require the callback log to report `answer: "blueberry_accept"`,
   `before: false`, `after: true`, `changed: true`. Capture and inspect the German
   accepted-result dialogue, close it once through an observed supported path,
   and require status `accepted: true`, `pendingSave: true`.
7. Before saving, deliver `blueberry-greeting title` once and require
   `state: "saveRequired"` with no title transition. This verifies the narrow
   unsaved-state guard without changing state.

Verified evidence:

```text
German menu: complete DialogueBox with `de-de` locale
German/fallback PNG SHA-256: 8aae64fe657d16a3ee0dfd9892a3f79b867761de170b1e209c324e4acc9e7046
DPadDown: succeeded; fresh menu read showed `blueberry_accept` focused
ControllerA: succeeded exactly once for the initial acceptance
Acceptance callback: before=false, after=true, changed=true; pendingSave=true
German accepted result: MCP observed the complete translated result; the PNG was
captured during native typewriter rendering and is not used as full-text evidence
Pending-save title refusal: `state=saveRequired`; world remained loaded
```

### Save, restart, and prove idempotence

Use the already authorized MCP fixture action once:

```json
stardew_fixture_save {}
```

Require the tool's completed durable-save result for the exact fixture. The MCP
fixture-save path drives Stardew's supported save iterator, but it does not
necessarily publish SMAPI's `GameLoop.Saved` event to the target mod. Therefore
`pendingSave` may remain true in that process and a `saveCompleted` log is not a
requirement for this path. Do not synthesize the event or clear state manually.
The proof is completed only by stopping the process, restarting the same exact
review selection, and observing the persisted value with `pendingSave: false`
after `SaveLoaded`.

Stop without resetting so the single fixture work save is retained:

```powershell
& $sdvkit project review stop --topology single --json |
  Tee-Object (Join-Path $evidence 'stop-before-reload.json')
```

Restart the same `$ready`, game path, topology, and `--test-save` selection.
Reconfirm exact artifact and save identities, then deliver
`blueberry-greeting status`. Require `accepted: true`, `pendingSave: false`.
The direct fixture autoload can begin in SMAPI's empty/default locale before the
title menu applies the isolated native preference. When that occurs, use the
same title-to-native-Load transition above and require exact fixture
revalidation plus German runtime status again. Open the dialogue and require the
German completed prompt with substituted values and the default-English
fallback. Select Accept once more only after observing the intended control;
require callback `before: true`, `after: true`, `changed: false`. This is the
idempotence proof. Do not save merely to manufacture another state transition.

Verified evidence:

```text
Fixture save: ready, exact fixture/save IDs, persisted at
2026-09-09T14:55:46.7483783+02:00, mayHaveRun=false; no target `saveCompleted` event
Stop before reload: exact process exited; work save retained; no reset
Restart: same fixture/save/package build identities re-established
Reloaded state: accepted=true, pendingSave=false
Restarted German/fallback PNG SHA-256: 34dfd64334bacfbfed3c00ee54b4c26b823c60ad81dc752a2f912997e9e98bee
Repeat sequence: DPadDown receipt at 15:05:27, fresh focused-Accept observation and
cursor cleanup at 15:05:28, then callback at 15:05:29 with before=true,
after=true, changed=false. This timing record does not attribute the callback to
one input edge. A later ControllerA at 15:05:44 opened GameMenu and is not the
idempotence trigger or an accepted-result screenshot. No mutation was replayed.
```

## Restore the initial language

Language selection changes the isolated review profile and must not be left in
German merely because the feature checks are complete. Before final stop/reset,
require `pendingSave: false`, close any world menu through one observed supported
path, and deliver `blueberry-greeting title` once. Require `titleRequested` and
a fresh title screenshot.

Use the same observed native title-menu language workflow to select
`$initialLanguage`. Do not edit `startup_preferences` directly and do not assume
the reverse action path is identical: capture the current title menu and language
selection before each input. Require all of the following before cleanup:

- native title text visibly matches `$initialLanguage`;
- after loading the same disposable work save, `blueberry-greeting status`
  reports the original runtime locale;
- a new read-only isolated `startup_preferences` observation reports the original
  native language preference;
- the restored preference file SHA-256 is recorded; byte equality with the initial
  file is not required if unrelated supported settings changed, but every
  difference must be understood and language restoration must be explicit.

Verified evidence:

```text
Pre-restoration title screenshot: visibly German
Restoration controls: freshly observed language control and ENGLISH choice; one
virtual-cursor MouseLeft press for each
Restored native-title screenshot: visibly English (NEW, LOAD, CO-OP, EXIT)
Restored runtime status after fresh exact restart: locale empty, accepted=true,
pendingSave=false, same fixture/save/build identities
Restored startup_preferences: languageCode=en; SHA-256
c457014d68388d09db846b4064a31367aa7a476ded662eb26e519284cd6f2fc1
Preference comparison: language restored explicitly; byte equality was not expected
because the native `timesPlayed` field advanced from 27 to 36
```

## Final cleanup

Preserve final status, selected-mod diagnostics, screenshots, action receipts,
and exact package/source hashes. Then clean up through ownership-aware commands:

```powershell
& $sdvkit project review stop --topology single --json |
  Tee-Object (Join-Path $evidence 'stop-final.json')
& $sdvkit project review reset --topology single --json |
  Tee-Object (Join-Path $evidence 'reset-final.json')
& $sdvkit project review status --topology single --json |
  Tee-Object (Join-Path $evidence 'final-status.json')
```

Require confirmed process exit, clean reset, removed staging, restored baseline,
and no unresolved ownership. Recheck the accepted ZIP SHA-256 and its extracted
translation bytes after cleanup. Report build, package, load, English UI,
language transition and restoration, German/fallback UI, controller behavior,
persistence, idempotence, and cleanup separately.

## Acceptance record

| Capability | Required evidence | Draft state |
| --- | --- | --- |
| Deliberate placeholder mismatch | Exit 3; exact file, field, default/locale token sets | Verified offline |
| Single-token correction | Exit 0; no problems; exactly intentional missing-key warning | Verified offline |
| Build | Selected project; 0 warnings, 0 errors | Verified offline |
| Exact package | Four expected entries; ZIP/DLL hashes; byte-equal manifest/locales | Verified offline |
| Extracted package | Inspect/check exit 0; exactly fallback warning | Verified offline |
| English rendered UI and mouse cancellation | MCP menu + PNG + callback/status | Verified live |
| Native language transition and restoration | Initial preferences/runtime/PNG + observed EN→DE→initial steps + restored preferences/runtime/PNG | Verified live |
| Fixture revalidation after title-only settings | Title state denies world-bound menu/fixture actions while owned viewport/input remains available; exact single save reload returns passed identity; mismatched save consumes recovery and remains denied | Verified live |
| German rendered UI and default fallback | MCP menu + PNG showing mixed German/default text | Verified live |
| Controller acceptance | DPadDown focus + one ControllerA + one 0→1 callback | Verified live |
| Save/reload and idempotence | Durable fixture save + real restart + persisted state + changed=false repeat | Verified live |
| Exact cleanup | Stop/reset/status, zero game processes, removed staging, and byte-identical protected roots | Verified live |

Final reset reported `root: null`, `lab: null`, `fixtureReset: true`, and
`stagingRemoved: true`; no Stardew or SMAPI process remained. Full before/after
inventories of the normal game Mods directory, the mod-manager staging directory,
and normal Stardew data were identical (20,007/18,068; 21,320/18,971; and
285/227 entries/files respectively).

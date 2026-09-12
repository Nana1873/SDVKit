# Neutral menu-close probe

This bounded original diagnostic target supports [issue #217](https://github.com/Nana1873/SDVKit/issues/217).
The [observations below](#observed-behavior-2026-09-12) reproduce a native
controller-close limitation; no SDVKit runtime fix is claimed. Use the existing
[review workflow](../../live-review.md), including exclusive lab ownership,
an exact disposable **single/screen 0** fixture, process-local input, and final
stop/reset. Do not start it while another task owns the lab.

Copy these three source files to the selected lab's ignored `.sdvkit/` directory,
then use `project check`, `project build`, and `project package` with that explicit
target. Review a freshly extracted copy of the exact ZIP, retaining package and
staged build identities. All output, logs and screenshots belong under `.sdvkit/`.
Do not deploy to normal Mods or select a normal save.

## Observation contract

- `mc217_open <label> root` requires no active menu and neutral observed buttons,
  logs that baseline, and opens an ordinary `IClickableMenu` subclass.
- `mc217_open <label> child` creates the same menu as a native child of another
  probe menu. Its inherited `exitThisMenu` should leave the parent in place.
- `mc217_open <label> controller-root` prepares the conditional explicit-controller
  case below. It returns `true` from `areGamePadControlsImplemented()` and calls
  native `exitThisMenu()` for B in `receiveGamePadButton`, logging that exact path.
- `mc217_observe <label>` arms observation of the current state without changing
  it. Rearm after collecting the initial menu/screenshot and before the input.
- Labels contain 1–48 ASCII letters, digits, hyphens or underscores. Commands
  refuse mismatched launch, fixture/save, player, role or local-screen ownership.

The default probe delegates keyboard, controller and close-button handling to
Stardew's base class; the optional controller variant closes on B in its earlier
controller callback. It adds no input, suppression, Harmony patches, close delays or
automatic retries. It never replaces an existing menu to prepare a case.
Its only setup mutation is opening its own menu from a verified closed baseline.

`MC217` records in the owned SMAPI log have a monotonically increasing sequence,
case label and game tick. They include public SMAPI pressed/released events,
helper states, native menu callbacks, root/child menu identity, game-visible and
raw controller connection state, raw target buttons, controller options/index,
and the native mapping of B. State changes are sampled at `UpdateTicking` and
`UpdateTicked`; the next 16 ticks after an observed target edge are also logged,
including unchanged `None` states. Observation ends after 1,800 ticks or lost
ownership. This is a bounded observation window, not an input delay or a claim
that unchanged states were continuously sampled between callbacks.

The game may emit `MenuChanged` after a native callback, and a child detachment
need not change the root reference. Use the tick samples and callback sequence
together. A single controller press may also generate a native mapped keyboard
callback; that is not proof of a second injected keyboard press.

## Repeat the bounded live cases

1. Confirm an unfocused, exact owned single fixture with no active menu. Record
   raw controller state, options, current runtime, target/package identity and
   a viewport screenshot. Do not manufacture focus or move the physical cursor.
2. Independently open a fresh **root** baseline for one Escape press, one
   process-local click at the observed visible close button, and one Controller B
   press. For the click use fresh menu geometry and `uiRevision`.
3. For each case retain the input completion response, then fresh menu/runtime
   and viewport evidence. Match those responses to the probe log's pressed,
   released and subsequent `None` samples and actual menu transitions. Input
   completion is not proof of the final menu effect.
4. Repeat the three independent cases with a **child** baseline. Stop when that
   child closes; record the remaining parent rather than closing it blindly.
   An explicitly inspected single Escape can subsequently restore the closed
   setup baseline; label this setup action separately.
5. In one separately labelled negative control, after a successful root Escape
   close and an observed closed baseline, deliberately send one additional
   Escape. Record its resulting native menu. This is a repeated-action test,
   not a recovery action or routine close guidance.
6. Stop/reset the exact owned review and verify exited process, staging/mailbox,
   fixture mount cleanup and protected-path evidence. Preserve failures and
   inconclusive windows; never replay an unknown-completion action.

### Conditional explicit-controller case

Run the base cases first. If the root B case establishes the full shared sequence
of one press, actual menu close, released/disconnected sample, and native
`CheckGamepadMode` inventory opening, the explicit variant is not needed merely
to repeat that cause. That conclusion covers the demonstrated post-close native
path under its observed conditions; it does not verify every custom controller
handler or identify the historical target mod's exact callback sequence.

If the base root case does not establish that sequence, inspect and restore a
closed baseline, then open `mc217_open controller-b controller-root`. Observe one
B press using the same completion, menu, screenshot and following-tick checks.
Do not infer custom-controller correctness from a base-menu non-reproduction.
The explicit variant is one additional root case, not a new controller matrix.

Installed Stardew 1.6.15 dispatch calls `receiveGamePadButton` before its later
mapped-key loop. In this version `areGamePadControlsImplemented()` guards the
A/X mouse fallbacks and A hold/release handling; it does **not** by itself disable
the later B-to-key mapping. An explicit B close can make the earlier menu inactive,
so later native `IsActive()` checks skip that mapped callback. Both paths may end
with no root menu before the following disconnected sample. The shared disconnect
handler's pause/index/no-menu conditions remain independent of which callback
closed the menu. Record actual callbacks and release state rather than assuming
that the flag alone prevents duplicate dispatch or inventing a second press.

## Observed behavior (2026-09-12)

The seven cases below ran in one exact owned disposable single/screen-0 review,
using Stardew 1.6.15.24356, SMAPI 4.5.2, and the unchanged v0.10.1 CI distribution.
Native controller mode was `Auto`, `playerIndex=One`, and the selected raw
controller remained disconnected with B released. All input cases had a fresh
`isActive=false` observation and a foreground PID outside the owned game, correlated
with read-only desktop samples. No focus changes or physical cursor input were
performed by the test tools. Initial focused setup observations were retained
separately; no test input was sent until an external focus change was verified.

| Observed baseline and one input | Post-completion menu | Controller connection |
| --- | --- | --- |
| Root + Escape | None | Stayed disconnected |
| Root + observed close click | None | Stayed disconnected |
| Root + B | Native `GameMenu` / inventory after the root closed | Connected, then disconnected |
| Child + Escape | Original parent only | Stayed disconnected |
| Child + observed close click | Original parent only | Stayed disconnected |
| Child + B | Original parent only; no inventory | Connected, then disconnected |
| Closed baseline after root Escape + deliberate extra Escape | Native `GameMenu` / inventory | Stayed disconnected |

Every case recorded exactly one target `ButtonPressed`, one `ButtonReleased`, and
following `None` samples. Both clicks used the inspected visible close component
at `(948,216)` with its own fresh UI revision, reported one completed click and
confirmed release, and reached the native child/root close callback. Coordinates
are historical evidence, not a fixed target for future runs. Typed menu/runtime
responses and inspected viewport screenshots corroborated the results; custom
menu inspection correctly reported partial `publicBase` coverage.

### Exact root-B sequence

The probe's monotonic sequence in the retained `final-SMAPI.txt` shows:

| Sequence / game tick | Observation |
| --- | --- |
| 183–184 / 17770 | One B `Pressed`; probe root active; game-visible controller connected, raw controller disconnected |
| 185–191 / 17771 | Native B callback, mapped `E` keyboard callback, then root cleanup; `UpdateTicked` has no menu |
| 192–194 / 17771 | B `Released`; next `UpdateTicking` has no menu and a disconnected game-visible controller |
| 195–196 / 17772 | `UpdateTicked` and `MenuChanged` show native `GameMenu` / inventory |
| 197 onward / 17772 onward | B reaches `None`; inventory remains during the following observations |

The B acknowledgement succeeded and still reported `menuOpen=false`; the next
typed menu read reported inventory. The viewport showed both native Gamepad
activation/disconnection messages behind that inventory. Delivery/release alone
therefore cannot establish the final menu state.

Source inspection of the exact installed binaries explains the observed path:
SDVKit uses supported SMAPI `Press` overrides; SMAPI's controller builder produces
the connected synthetic sample and returns to the raw disconnected state when
the override ends. Stardew's `CheckGamepadMode` can then open `GameMenu` when its
index/pause conditions qualify and no active root remains. The callback and tick
sequence above is observed; attribution to that native branch is based on the
matching installed source, without a patched branch tracer. The child-B comparison
showed the same connection transitions while retaining its parent, with no
inventory opening. The deliberate second Escape is a separate native menu-open
action, not evidence of a stuck first press.

No independent SDVKit input lifecycle defect was demonstrated. Holding fake
connectivity beyond the action or suppressing a target menu would change native
controller semantics and conceal the observed limitation; neither is warranted
by this investigation. Use one Escape or one observed process-local close click
for routine closing, then inspect and stop when the intended target is closed.

### Provenance, cleanup and limits

- Tested probe source: `5ea7889f00e2efe33973731cfadcb9c8783aa00d` in [PR #221](https://github.com/Nana1873/SDVKit/pull/221).
- Probe ZIP SHA-256: `0069f0820078d68b4851251f4939a41f66059629d1c2999248bddd1fc5593b3d`.
- Probe DLL SHA-256: `03301bbeb0c21fd4cac0f2e53e67401efb4303c7cc9d16e10778959bd5cbe01b`.
- Runtime ZIP SHA-256: `6da54c86b9ff9034621af9ab18ccb946383d7db8de792612456c9fd733fa5500`.

The target/launch/process/save/build bindings, complete event log, typed MCP
responses, screenshots, before/after desktop observations and final audit remain
under the task's ignored `.sdvkit/issue-217/live/`; the source/package evidence is
under `revision-2/`. The initial preparation and earlier candidate evidence remain
separate. Restore/format/build, installed-game AlwaysOn and probe builds, package
and extraction passed. The default local suite passed 2,213 with the two documented
local stress skips; exact tested-head CI passed all 2,215 with zero skips plus
packaging/portable checks. A local orchestration-script parse error occurred before
any case input; it was corrected before use and is not a product failure.

All 18 native MCP sessions exited on EOF with exit code 0, no forced termination
and no stderr. Final exact stop/reset removed target staging, mailbox payloads
and the registered fixture mount; both owned locks were free, all three work-save
files matched baseline, and no game process remained. The profile retained its
ordinary fixture-preparation save directory, outside the removed registered mount.
All 41,615 entries in the protected normal Mods, Stardew data and mod-manager
staging comparison were unchanged. The exclusive live slot was released.

Root Escape, root B and child Escape had unchanged sampled physical pointer and
foreground during the input calls. Root/child clicks, child B and extra Escape
had external physical pointer movement; they establish functional/menu/release
behavior while unfocused, not stationary-pointer proof. None of these samples is
continuous sub-tick monitoring or alone establishes cursor-movement causality.

`controller-root` was prepared and compiled but not run: the base root-B case
already established the shared post-close native path under the agreed conditional
gate. The earlier explicit-controller callback distinction remains source-derived.
Physical-controller, force-on/off, multiplayer, other local screens and other mods
were not tested. No claim is made about the historical target mod's exact callback
sequence or #193's earlier event attribution. This documents a reproduced
controller limitation, not a controller-close fix or broad correctness proof.

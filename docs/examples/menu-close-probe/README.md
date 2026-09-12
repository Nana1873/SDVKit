# Neutral menu-close probe

This bounded original diagnostic target supports [issue #217](https://github.com/Nana1873/SDVKit/issues/217).
It is prepared for live acceptance; source inspection and compilation alone do
not establish a reproduced defect or a fix. Use the existing
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
- `mc217_observe <label>` arms observation of the current state without changing
  it. Rearm after collecting the initial menu/screenshot and before the input.
- Labels contain 1–48 ASCII letters, digits, hyphens or underscores. Commands
  refuse mismatched launch, fixture/save, player, role or local-screen ownership.

The probe delegates keyboard, controller and close-button handling to Stardew's
base class. It adds no input, suppression, Harmony patches, close delays or
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

## Proposed live cases

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

## Source hypothesis to verify

The installed game/SMAPI inspection suggests this sequence for a synthetic B
press with no physical controller: SMAPI builds a connected override, the native
menu dispatcher maps B to a keyboard action, and the following raw disconnected
sample reaches Stardew's `CheckGamepadMode`. That method can create `GameMenu`
when the controller disconnects and no active menu remains. Its conditions,
including the player index and pause eligibility, matter. The probe records
these observations without changing that behavior.

Connection notifications and subsequent inventory opening must be assessed
separately. A remaining parent, force-on/off controller settings, a physical
controller, or menu-specific close rules may change the result. This probe does
not establish those untested configurations, other mods, or multiplayer behavior.
Expand role/screen gates only if a demonstrated shared runtime correction needs
them. Do not infer current correctness from historical #92/#93 acceptance or
assign #193's later inventory to an unproven input edge.

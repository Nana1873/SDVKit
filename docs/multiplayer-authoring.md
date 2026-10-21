# Author a per-player multiplayer mod

> Verified for issue #196 and kept as a live acceptance script ([#284](https://github.com/Nana1873/SDVKit/issues/284)). The steps to rerun it are in [Test a per-player multiplayer mod](test-a-multiplayer-mod.md); this page keeps the design and the manual split-screen steps the script does not run.

This original SMAPI mod gives Stone to the current farmer on F7. On F8 it shows that farmer's inventory, persisted `modData` and local inventory-event state. The deliberate bug collapses both players into `Game1.MasterPlayer`. The complete sources (broken target, corrected target, chest probe) are in [`docs/examples/multiplayer-recipe`](examples/multiplayer-recipe/).

The fix splits authority this way:

1. The host validates and authorizes a farmhand's request.
2. The farmhand's own client adds Stone to its own backpack and writes its own `Farmer.modData`.
3. The client reports a correlated result, which the host validates.

The host does **not** measure or write a remote player's inventory.

| Gate | State |
| --- | --- |
| Broken network shared-player failure | Verified by [`multiplayer.ps1`](../scripts/acceptance/multiplayer.ps1) |
| Corrected network host 1 / farmhand 2, per-player F8 dialogs | Verified by the script |
| Farmhand world move, chest open and withdrawal; host reads the chest | Verified by the script |
| Farmhand leave, stale MCP, rejoin | Rejoin and session rotation verified by the script; stale MCP clients are a manual check |
| Explicit save and new-process reload | Verified by the script |
| Natural overnight disk save | Observed, not restart proof |
| Local split-screen state per screen, walking, `worldActionCursorAimUnavailable` | Verified by the script |
| Local split-screen replacement, persistence and chest | Verified manually (below), not in the script |

This covers one local host with one farmhand in `network-2`, and two screens in one `single --test-save` process. It does not prove other or internet multiplayer setups.

## Local split-screen

This sequence produced the accepted split-screen result:

```powershell
& $sdvkit project review start $fixedReady --game-path $gamePath --topology single --test-save --companion $probeReady --json
& $sdvkit project review mcp serve --topology single --allow-fixture-actions
& $sdvkit project review split-screen join --json
& $sdvkit project review split-screen status --screen 0 --json
& $sdvkit project review split-screen status --screen 1 --json
```

The [unbound](glossary.md#topology-roles-and-screens) server above holds fixture authority. Check that fixture authority cannot attach to a screen. This startup must be refused, because `--allow-fixture-actions` is not supported with `--screen`:

```powershell
& $sdvkit project review mcp serve --topology single --screen <host-screen-id> --allow-fixture-actions
```

Read the actual screen IDs from status; a replacement is not always screen 1. Start one input client per reported screen:

```powershell
& $sdvkit project review mcp serve --topology single --screen <host-screen-id> --allow-input
& $sdvkit project review mcp serve --topology single --screen <farmhand-screen-id> --allow-input
```

Record `screenId`, `farmerId` and `contextId`. Press F7 once on the host screen and twice on the second screen. Then press F8 on each. Require inventories 1 and 2, and each dialog showing its own farmer and event values. The accepted run produced host Stone 1 and local farmhand Stone 2.

The host screen removes the second screen. Send leave from screen 0, not from the departing screen:

```powershell
& $sdvkit project review split-screen leave --screen 0 --json
```

Require the departed screen's MCP client to become stale, and check the host state. Join again through the host screen. Read status and select the replacement screen that the runtime reports:

```powershell
& $sdvkit project review split-screen join --screen 0 --json
& $sdvkit project review split-screen status --screen 0 --json
& $sdvkit project review split-screen status --screen <reported-replacement-id> --json
```

Read the replacement's farmer and context again. Start a fresh screen-bound client for it, even if a screen number is reused.

Then prove persistence:

1. Call `stardew_fixture_save {}` on the authority MCP client.
2. Stop without reset and restart the kept `single --test-save` review.
3. Rejoin and read the screens again.
4. Require 1/2 and `persisted=True` with matching actual and event values.
5. Run the chest gate for both screens without transfer. Then clean up, stop and reset.

In the accepted run, the old farmhand client failed and the replacement appeared as screen 2 with a new context. Save and restart kept host 1 / farmhand 2, and probe v3 produced the chest capture above. Final cleanup stopped and reset both topologies. All 41,612 protected normal-save, `Mods` and staging entries matched the baseline, with nothing changed, missing or added.

## Failed candidates retained

- v1: host 1, farmhand 0, with "No inventory room" from a host-side remote change.
- v2: the warning disappeared but the farmhand stayed at 0. Silence was not proof.
- v3: farmhand inventory and event reached 1, but the farmhand's persisted state was missing.
- v4: the client changes its own inventory and `modData`, and the host validates the correlated result. Accepted network implementation.
- Probe v1: the network farmhand failed the save-ID guard. It compared the local folder name directly with the review save ID.
- Probe v2: allowed the unique-game-ID fallback for a network farmhand, but still rejected the local split-screen client.
- Probe v3: kept all guards, added the local-farmer fallback and passed both chest gates.
- The first network chest open, before replication, found zero chests. It was kept as a failed attempt.
- Overnight: day 2 disk state was 1/2. Reload was proved only by the later explicit-save, new-process gate.
- A local join sent during loading was refused before dispatch. The later join ran once. A second chest prepare was refused because the marked chest already existed.
- `inputBusy` was rejected before execution. `inputBindingChanged` meant completion was uncertain, so the menu was read before any further action. The open dialogue proved completion, and no blind retry was sent.
- Moving around during the overnight attempt gained Fiber 8 but left Stone 2 unchanged. It was not used as restart proof.
- Later processes overwrite the normal latest-SMAPI-log file. Only native logs copied before each transition count as log evidence. The kept JSONL captures cover the structured calls.

## Cleanup

Close the MCP clients first. Stop and reset only after the matching persistence evidence is complete. Require exited processes, released mounts, a restored fixture baseline, removed network staging, a removed probe chest and unchanged normal saves and `Mods`.

```powershell
& $sdvkit project review stop --topology network-2 --json
& $sdvkit project review reset --topology network-2 --json
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
```

## Reference details

`SaveLoaded` sets the sample's displayed event fields. Matching event values after a reload prove restored display state, not a fresh `InventoryChanged` event after restart.

SHA-256 of the accepted artifacts, kept as provenance:

| Artifact | Source | DLL | Two-entry ZIP |
| --- | --- | --- | --- |
| Fixed v4 | `62239739d868e1ad60d77f05f00e613f3abd3a5d358bd7133c24a1cebd2e8226` | `d09d2e8072b5f27cb73d67b488d28be2977984c94fbde13caa72da501ad47b79` | `9e3ca8e0e5912c4a28f5483e31ff7af15b26a8ee5a8197f02dde9fdf351ce568` |
| Probe v3 | `a01826ef4e52e41c1f43a27137addea82d1c265e01fd850dba07513e661ec71e` | `0070994589090b6a861a9f0b58b2c2de6f1234bbfa289ac523d6f967a0342243` | `ce592229c6d07659d24398d530377d7b065fbbce0247cc3c2f2cf76687b16e17` |

The acceptance used separate runs. Do not merge their identities or assume one save produced every result:

| Evidence slice | Companion | Accepted result |
| --- | --- | --- |
| Network fixed-v4 state and persistence | Original guarded probe candidate | Host 1/farmhand 2; farmhand leave/rejoin with session rotation and stale-client rejection; explicit save and new-process reload |
| Local fixed-v4 state and persistence | Probe v2 | Host Stone 1 / local farmhand Stone 2 on host screen 0; original screen 1 removed, replacement screen 2 freshly bound; explicit save and new-process reload |
| Fresh network and local chest observation | Probe v3 | The selected clients each saw backpack Stone 0 and chest Stone 2 without transfer; the fresh local farmhand ID was `-3969011337315626278`, distinct from the persistence run's farmer `2954633391945246945` |

## Chest gate on a screen

The accepted manual run opened the probe chest for the selected network farmhand and for the selected local client; each capture showed the selected farmer, an empty held item, all 12 backpack slots and all 36 chest slots (backpack Stone 0, chest Stone 2, nothing transferred). The network farmhand still could not call `stardew_fixture_save`. `multiplayer.ps1` now repeats the chest with a real withdrawal for the network farmhand and for screen 1: a screen's farmhand starts in a generated cabin location that fixture tools cannot reach, so screen 1 walks out of its cabin with a key chord and shares the Farm with the host before it opens the chest.

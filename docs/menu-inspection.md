# Inspect the active menu

The active-menu reader has bounded public adapters for vanilla inventory,
shop, dialogue/question, and crafting menus. A `dialogueBox` node reports the
current rendered text plus response keys, text, controller IDs, bounds,
visibility, and focus. A `craftingPage` node reports the current page and
visible recipe IDs, display names, component IDs, ingredient IDs/quantities,
outputs, ingredient availability, and craftable count.

Availability and craftable count are `null` with the explicit
`craftingAvailabilityUnavailable` limitation when the native page exposes
material containers that SDVKit cannot safely aggregate without changing game
state. Invalid or overlong stable recipe/choice identities are omitted with a
limitation rather than truncated into a new identity.

Dialogue and crafting values are observations only; they do not select,
activate, or infer clickability. Text is bounded and localized display text is
not a stable identity. Unsupported menu subclasses retain `publicBase` partial
coverage. Recipe and response lists are capped by the existing menu response
limits and report incomplete coverage when truncated.

Use `project review menu --json` or native `stardew_menu_get {}` during an exact,
world-ready [review](live-review.md). Each call captures fresh state on the game
thread through the existing request/response path. It does not reuse the runtime
heartbeat or invoke layout, population, hover, snap, selection, scroll or input.

```powershell
& $sdvkit project review menu --json
& $sdvkit project review menu --topology network-2 --role host --json
& $sdvkit project review menu --topology network-2 --role farmhand --json
```

Native MCP uses the role fixed at server startup, without an input opt-in. Both
surfaces check fresh process, launch, target/build and fixture bindings before
and after capture. Captures older than five seconds are unavailable, including
when they age during the final binding check. A changed binding is unavailable. Title,
loading, unjoined network roles and other non-world-ready states are unavailable;
the CLI viewport screenshot exception does not apply. No menu in a ready world
is a successful report with `menuOpen=false`, `menus=[]` and no identity scope.

## Declared fields and coverage

Every node reports full type name and assembly simple name, screen bounds, parent/relationship, adapter,
coverage and bounded components. Relationships are `root`, `activePage`,
`inventory` and the public `child` menu. Only exact vanilla types use the specific
adapters below; subclasses receive the partial public base adapter.

| Menu | Additional public fields read |
| --- | --- |
| `GameMenu` | Tabs, `currentTab`, the current entry in `pages` as `activePage` |
| `InventoryPage` | Nested inventory grid, equipment icons, portrait, trash can, organize button, junimo note icon |
| `InventoryMenu` | Inventory slot components |
| `ShopMenu` | Nested inventory grid, sale row components, up/down arrows, scrollbar, `currentItemIndex` as `scrollIndex` |
| `NamingMenu` | Public naming, done and random components; exact non-password `TextBox` identity, bounds, `Selected`, keyboard dispatcher and subscriber identities |
| Unknown or mod-created `IClickableMenu` | Existing public `allClickableComponents`, close component, bounds, snapped-component reference and `GetChildMenu` only; always `partial` |

All adapters also read those public base fields. Missing/null components are
absent; an unpopulated public component list is never populated by inspection.
The declared fields do not cover every drawn control or private menu state.
Other vanilla pages, custom fields, shop stock/items/prices, inventory items,
hover text and scroll internals are outside this slice. A shop row represents a
reusable control object, not the identity of the stock currently drawn in it.
Use the separate [shop inspection](shop-inspection.md) capture for supported
single-review Gold-shop offers, money, inventory and held-item observations.

Components report only an opaque numeric ID, fixed semantic kind, public
controller ID, bounds, `visibleFlag`, `intersectsViewport` and
`controllerFocused`. Focus means reference equality with that node's public
`currentlySnappedComponent`; it is not selection or hover. The visibility flag
is the public field, and viewport intersection is geometric: neither establishes
that the component is drawn, enabled, unobscured or clickable. No such inferred
states are returned. The viewport rectangle starts at screen-local `(0,0)` and
uses the game's UI viewport dimensions, excluding world-camera offsets. Bounds
use this UI coordinate system, which
may differ from screenshot pixel coordinates when UI scaling is active.

An exact NamingMenu (also when constructed by a mod) can expose `textField`:
`id`, `dispatcherId`, nullable `subscriberId`, `selected`, `available` and `bounds`.
`available` requires the root without a child menu, a selected exact non-password
TextBox and subscriber equality. Unknown/custom/GMCM fields have no `textField`.
These identities and selection participate in `uiRevision`; replacements or lost
selection invalidate text requests. Use `textField.id` with that fresh revision
for [opt-in text input](mcp.md#opt-in-input).

No component names, labels, text, item/player data, paths or arbitrary custom
objects are serialized. The bounded menu type identifier is runtime metadata.
Assembly identity contains no assembly file path. The capture returns copied records and read-only collections, never live game
objects. Separate screenshots remain necessary to establish visible behavior.

## Identity and bounds

Use `(launchId, identityScope, id)` together. IDs identify observed object
references, independent of controller IDs, names or list indices. Reordering,
scrolling and resizing retain IDs for the same objects; replacements get new
IDs. Repeated references within one node are deduplicated. Duplicate controller
IDs remain distinct objects. A component shared by two nodes has the same ID.
Components are ordered by assigned ID; newly observed objects get IDs in the
adapter's fixed field/list traversal order. Root replacement/closure or a
`MenuChanged` notification of a different root resets the scope; a notification
for an already captured root preserves it. IDs are observations, not action handles,
save identities or a promise across launches.

The report contains at most four menu levels, 16 nodes and 128 component entries
in total. Each node scans at most 512 public component entries. Type identifiers
are restricted to 128 letters/digits or `_ . +` and backtick; assembly simple names
also allow hyphens/spaces and have the same 128-character bound. Other identifiers
are withheld. The encoded response envelope is capped at 128 KiB. Tree cycles,
scan/count limits and withheld identifiers produce explicit limitations;
truncation is never complete. An oversized envelope is unavailable.

`complete=true` means all **declared fields** were captured within these limits,
not full UI coverage. Unknown/custom base coverage is always incomplete even
when no size limit is reached. Exit `0` means a valid observation, including a
partial report; inspect `complete`, `truncated`, `coverage` and `limitations`.
Exit `3` / MCP `isError=true` means unavailable. No automatic retry occurs.

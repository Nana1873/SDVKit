# Inspect the active menu

Use `project review menu --json` or the MCP tool `stardew_menu_get {}` during a [world-ready](glossary.md#worlds-and-fixtures) [review](live-review.md). Each call captures fresh state on the game thread. It does not reuse the runtime heartbeat. It does not trigger layout, population, hover, snap, selection, scroll or input.

```powershell
& $sdvkit project review menu --json
& $sdvkit project review menu --topology network-2 --role host --json
& $sdvkit project review menu --topology network-2 --role farmhand --json
```

The MCP tool uses the [role](glossary.md#topology-roles-and-screens) fixed at server start and needs no opt-in.

The reader has adapters for vanilla inventory, shop, dialogue and question, and crafting menus. A ready world without a menu is a success with `menuOpen=false`, `menus=[]` and no identity scope. Title screen, loading, unjoined network roles and other states that are not world ready are unavailable. The CLI viewport-screenshot exception does not apply here.

Exit `0` means a valid observation, possibly partial: check `complete`, `truncated`, `coverage` and `limitations`. `complete=true` means all **declared fields** were captured within the limits. It does not mean full UI coverage. Exit `3` or MCP `isError=true` means unavailable. SDVKit does not retry automatically.

## Declared fields and coverage

Every node reports its full type name and assembly simple name, screen bounds, parent and relationship, adapter, coverage and its components. Relationships are `root`, `activePage`, `inventory` and the public `child` menu. Only the vanilla types below get their specific adapter. Subclasses get the partial public base adapter.

| Menu | Additional public fields read |
| --- | --- |
| `GameMenu` | Tabs, `currentTab`, the current entry in `pages` as `activePage` |
| `InventoryPage` | Nested inventory grid, equipment icons, portrait, trash can, organize button, junimo note icon |
| `InventoryMenu` | Inventory slot components |
| `ShopMenu` | Nested inventory grid, sale row components, up/down arrows, scrollbar, `currentItemIndex` as `scrollIndex` |
| `NamingMenu` | Public naming, done and random components; exact non-password `TextBox` identity, bounds, `Selected`, keyboard dispatcher and subscriber identities |
| Unknown or mod-created `IClickableMenu` | Existing public `allClickableComponents`, close component, bounds, snapped-component reference and `GetChildMenu` only; always `partial` |

All adapters also read the public base fields. Missing or null components are left out. Inspection does not fill an unpopulated public component list. Unknown and custom menus are always incomplete, even within the size limits.

The declared fields do not cover every drawn control or private menu state. Other vanilla pages, custom fields, shop stock, items and prices, inventory items, hover text and scroll internals are out of scope. A shop row is a reusable control, not the identity of the stock drawn in it. For Gold-shop offers, money, inventory and the held item, use [shop inspection](shop-inspection.md).

### Dialogue and crafting

A `dialogueBox` node reports the current rendered text plus each response's key, text, controller ID, bounds, visibility and focus.

A `craftingPage` node reports the current page and its visible recipes: IDs, display names, component IDs, ingredient IDs and quantities, outputs, ingredient availability and craftable count.

- Availability and craftable count are `null` with `craftingAvailabilityUnavailable` when the page uses material containers that SDVKit cannot add up without changing game state.
- An unavailable crafting page is `null` with `craftingPageUnavailable`.
- Too many recipes, ingredients or outputs add `craftingCollectionLimit` and make the report incomplete.
- An invalid or overlong recipe or choice identity is left out with a limitation. It is not truncated into a new identity.

These values are observations only. They do not select, activate or say that something can be clicked. Text has a length limit, and localized display text is not a stable identity.

### Components

A component reports only an opaque numeric ID, a fixed semantic kind, its public controller ID, bounds, `visibleFlag`, `intersectsViewport` and `controllerFocused`.

- `controllerFocused` means the component is the node's public `currentlySnappedComponent`. It is not selection or hover.
- `visibleFlag` is the public field. `intersectsViewport` is pure geometry. Neither proves that the component is drawn, enabled, unobscured or clickable, and SDVKit returns no such guess.
- The viewport starts at screen-local `(0,0)` and uses the game's UI viewport size, without world-camera offsets. Bounds use these UI coordinates, which can differ from screenshot pixels when UI scaling is active.

### Naming menu text field

A vanilla `NamingMenu`, also one created by a mod, can expose `textField` with `id`, `dispatcherId`, nullable `subscriberId`, `selected`, `available` and `bounds`. `available` needs the root menu without a child menu, a selected non-password `TextBox` and a matching subscriber. Unknown, custom and GMCM fields have no `textField`.

These identities and the selection are part of `uiRevision`. A replaced field or lost selection invalidates text requests. Use `textField.id` with that fresh revision for [opt-in text input](mcp.md#opt-in-input).

### What is not returned

No component names, labels, text, item or player data, paths or custom objects are serialized. The menu type identifier is runtime metadata. Assembly identity has no file path. The capture returns copied records and read-only collections, not live game objects. You still need screenshots to prove visible behavior.

## Identity and bounds

Use `(launchId, identityScope, id)` together. An ID identifies an observed object reference, independent of controller IDs, names or list positions.

- Reordering, scrolling and resizing keep the IDs of the same objects. Replaced objects get new IDs.
- Duplicate controller IDs stay distinct objects. A component shared by two nodes has the same ID.
- Closing or replacing the root menu resets the scope.
- IDs are observations, not action handles or save identities, and do not carry across launches.

A report has at most four menu levels, 16 nodes and 128 component entries in total. Cycles, scan or count limits and withheld identifiers produce limitations. A truncated report is not complete.

## Reference details

- Both surfaces check the process, launch, target, build and fixture bindings before and after capture. A capture older than five seconds is unavailable, also when it ages during the final check. A changed binding is unavailable.
- Repeated references within one node are deduplicated. Components are ordered by ID. New objects get IDs in the adapter's fixed field and list order. A `MenuChanged` notification for a different root resets the scope; one for an already captured root keeps it.
- Each node scans at most 512 public component entries. Type identifiers allow up to 128 letters, digits, `_ . +` and backtick. Assembly simple names also allow hyphens and spaces, with the same 128-character limit. Other identifiers are withheld.
- The encoded response is capped at 128 KiB. A larger response is unavailable.

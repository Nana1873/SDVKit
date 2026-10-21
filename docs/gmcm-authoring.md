# Author a GMCM option: contract and API

The steps to change an option in the UI, save it and prove its effect after a restart are in [Change and verify a GMCM option](test-a-gmcm-option.md); [`scripts/acceptance/gmcm-checkbox.ps1`](../scripts/acceptance/gmcm-checkbox.ps1) runs them for a Boolean option and [`gmcm-number.ps1`](../scripts/acceptance/gmcm-number.ps1) for an integer slider. This page keeps what those do not say: the bounded contract of the checkbox example, and the GMCM API interface it was checked against.

## Contract

| Concern | Bounded contract |
| --- | --- |
| Provider | Explicitly selected `spacechase0.GenericModConfigMenu` **1.16.0**, with recorded manifest/DLL hashes; the example refuses menu integration with other versions. |
| Environment | Single review, Stardew Valley **1.6.15**, SMAPI **4.5.2**, owned disposable standard-farm fixture. No multiplayer acceptance. |
| Option | Original mod `ExampleAuthor.GmcmArrival` ([source](examples/gmcm-checkbox-recipe/GmcmArrival/ModEntry.cs)), explicit GMCM `fieldId` **Enabled**, Boolean checkbox, default **false**. Only JSON Boolean values are accepted for the exported variant. |
| Registration | `Register` during `GameLaunched`, `titleScreenOnly: false`; `AddBoolOption` reads/writes the mod's typed config. |
| Effect | `DayStarted` warps the local farmer to Farm 64,15 only when enabled and in a ready single-player world. Saving config does not immediately warp the farmer. |
| Save | The real GMCM **Save** action commits its cached Boolean through the setter and calls the registered `Helper.WriteConfig` callback; the menu stays open. A checked box alone is not saved-state proof. |
| Reload | `Helper.ReadConfig` reads once at Entry. Single-review stop removes staged config; the exported config and renewed staging below `.sdvkit/` carry it into the next process. |
| Unsupported | Other controls, custom/text fields, other provider versions and foreign mod options are outside this contract. No generic GMCM text or option-editing API is added to SDVKit. |

The optional manifest dependency lets the mod explain unavailable integration; the acceptance always selects GMCM explicitly. The runtime check enforces the exact version, since a manifest minimum is not an exact-version constraint. The core warp still uses a previously loaded valid config when the optional provider is unavailable; this gate disables menu integration, not the mod's independent feature. Such a run cannot pass the provider-bound acceptance.

## References

Use the [official GMCM download](https://www.nexusmods.com/stardewvalley/mods/5098?tab=files) to obtain the selected provider; do not copy provider implementation code or binaries into this repository. A Vortex hardlinked source may fail preparation with `Refresh files must have exactly one filesystem link.`; copying it to your own lab and selecting that independent copy resolves this. Never unlink, redeploy or alter the original normal Mods files to make staging pass.

For mod authoring, use the [SMAPI config guide](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Config), the [4.5.2 game-loop events](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/Events/IGameLoopEvents.cs) and the [SMAPI recipe](smapi-authoring.md#choose-the-tool-and-references). Use [menu inspection](menu-inspection.md) for fresh revisions and [owned input](mcp.md#opt-in-input) for the exact process-local mouse path. GMCM `fieldId` is an author-selected identity, not an SDVKit numeric component ID. GMCM remains partial public-menu coverage, so locate controls on screenshots.

## API interface

The small interface the example declares was checked against the local 1.16.0 provider's `GenericModConfigMenu.Framework.Api`; its `SpecificModConfigMenu` uses a `SpaceShared.UI.Checkbox` for Boolean options. These implementation details explain feasibility, not a stable introspection API.

```csharp
public interface IGenericModConfigMenuApi
{
    void Register(IManifest manifest, Action reset, Action save, bool titleScreenOnly = true);

    void AddBoolOption(
        IManifest manifest,
        Func<bool> getValue,
        Action<bool> setValue,
        Func<string> name,
        Func<string>? tooltip = null,
        string? fieldId = null);

    void OpenModMenu(IManifest manifest);
}
```

## What a pass proves

The acceptance proves explicit config transfer across stop and restaging and the new runtime effect. It does not imply automatic mod-config retention, a changed compiled default, overnight behavior, world-save persistence or multiplayer. The original ZIP keeps shipping `Enabled=false`; the reconciled config is a separately selected test input carrying the same packaged DLL. Report compilation, automated tests, packaging and verified game behavior separately.

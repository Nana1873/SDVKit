# Explain an unexpected Content Patcher winner

This original recipe changes one field with two competing CP patches in one pack.
Use an explicitly selected local Content Patcher 2.9.1 directory and an owned
disposable single review. Keep generated pack files and evidence below the lab's
ignored `.sdvkit/`; ordinary saves and normal Mods stay outside this workflow.
Use one idle SMAPI console and the installed absolute `$sdvkit` executable.

## Author the competing changes

Create `.sdvkit/recipes/CpPatchOrder/manifest.json`:

```json
{
  "Name": "CP Patch Order Recipe",
  "Author": "ExampleAuthor",
  "Version": "1.0.0",
  "Description": "Two original patches for a disposable patch-order review.",
  "UniqueID": "ExampleAuthor.CpPatchOrder",
  "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher" }
}
```

Create `content.json` beside it:

```json
{
  "Format": "2.9.0",
  "Changes": [
    {
      "LogName": "Preferred label",
      "Action": "EditData",
      "Target": "Data/Objects",
      "Fields": { "388": { "DisplayName": "Order recipe preferred" } }
    },
    {
      "LogName": "Unexpected later label",
      "Action": "EditData",
      "Target": "Data/Objects",
      "Fields": { "388": { "DisplayName": "Order recipe unexpected" } }
    }
  ]
}
```

Set `$pack` to this generated directory and `$provider` to your explicit CP 2.9.1
directory. Check them before launching; use the existing verified baseline or
prepare one while every lab role is stopped, following the [review guide](live-review.md).

```powershell
& $sdvkit project check $pack --json
& $sdvkit project inspect $provider --json
& $sdvkit project review status --topology single --json
& $sdvkit project review start $pack --companion $provider --test-save --topology single --json
& $sdvkit project review status --topology single --json
```

Wait for fresh ready ownership and exact loaded IDs/versions. CP may normalize
config on its first launch. For staging drift, preserve generated staged config
locally, stop/reset, deliberately adopt those config bytes in the generated
source and restart. Never relax the staging guard.

## Retain the mismatch, then diagnose

```powershell
& $sdvkit project review data get Data/Objects 388 --json
& $sdvkit project review cp-diagnose --pack ExampleAuthor.CpPatchOrder --provider Pathoschild.ContentPatcher --asset Data/Objects --order --json
```

The desired label is `Order recipe preferred`; the separate final observation
should instead report `DisplayName: Order recipe unexpected`. Retain both replies.
In `applied.messages`, the preferred patch precedes the unexpected patch, and both
are checked as applied after the Data read. `order.messages` preserves their
global positions and index paths. The diagnosis itself did not load the asset.
Do not infer a final value from its rows alone; retain incomplete results without
blindly retrying them.

Native MCP can make the same diagnosis through the existing default server:

```json
{"packId":"ExampleAuthor.CpPatchOrder","providerId":"Pathoschild.ContentPatcher","asset":"Data/Objects","order":true}
```

The [MCP guide](mcp.md#content-patcher-diagnosis-and-opt-in-refresh) owns startup,
argument and result contracts. Compare the selected asset, messages and exact
launch/staged identities with CLI evidence before correcting source.

## Correct and verify the final value

Stop/reset this exact review, then reverse the two authored `Changes` objects,
putting `Preferred label` last. Keep their contents unchanged, check the corrected
source, and start with the same provider and baseline:

```powershell
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
# Reverse the two source Changes here, then validate and restart.
& $sdvkit project check $pack --json
& $sdvkit project review start $pack --companion $provider --test-save --topology single --json
& $sdvkit project review cp-diagnose --pack ExampleAuthor.CpPatchOrder --provider Pathoschild.ContentPatcher --asset Data/Objects --order --json
& $sdvkit project review data get Data/Objects 388 --json
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
```

The new diagnosis should list `Unexpected later label` before `Preferred label`.
Compare the separate final `DisplayName` with `Order recipe preferred`. Unchecked
boxes before the first Data read can mean the asset had not yet loaded. Record
actual checks; schema/build success does not establish an in-game winner. Require
verified owned stop/reset and removed staging before returning the lab slot.
These two known CP edits demonstrate this recipe, not arbitrary mod provenance
or UI rendering.

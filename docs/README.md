# SDVKit documentation

Start with [installation and the two quickstarts](../README.md). These pages describe `main`; [published versions](https://github.com/Nana1873/SDVKit/releases) link to their corresponding tags.

| Task | Guide |
| --- | --- |
| Create, inspect, check, build, or package a mod | [Toolkit](toolkit.md) |
| Run a smoke, review a mod, or test persistence | [Live review](live-review.md) |
| Refresh selected CP patches during a review | [CP refresh](cp-refresh.md) |
| Explain a selected Content Patcher change | [CP diagnosis](cp-diagnosis.md) |
| Read Data, maps, textures, audio, or observed mod assets | [Inspection reference](inspection.md) |
| Connect an agent to a running review | [Native MCP](mcp.md) |
| Diagnose lifecycle, staging, and cleanup | [Lab reference](lab-reference.md) |
| Build or contribute to SDVKit | [Contributing](../CONTRIBUTING.md) |
| Verify and publish a version | [Release procedure](releasing.md) |
| Find changes since an earlier version | [Changelog](../CHANGELOG.md) |

PowerShell examples use `& $sdvkit`, the absolute executable path set during installation. Keep the current directory at the intended lab root for every live command. Placeholder operands such as `<asset>` must be replaced; examples naming companions require your explicit local mod selection.

## Capability matrix

| Capability | CLI | Native MCP | Topology / prerequisites |
| --- | --- | --- | --- |
| Create, inspect, package | `project` | No | C# mods or content packs; no live review |
| Offline authoring check | `project check` | No | One C# mod or CP 2.9.x root; manifest, CP content, direct i18n; no game/network |
| Build | `project build` | No | One C# project and ready game/SMAPI |
| Automated project smoke | `project smoke` | No | Standalone C# target; single or network-2 |
| Review lifecycle | `project review start/status/stop/reset` | No | C# target in either topology; content-pack target single only, with explicit provider |
| Runtime and selected-mod diagnostics | `project review status` | Runtime, review, mods tools | Active single or fixed host/farmhand role |
| Selected Content Patcher diagnosis | `project review cp-diagnose` | CLI workflow; dedicated tool deferred | Active single; explicit pack and CP 2.9.1 provider |
| Selected-mod warnings and exceptions | `project review diagnostics` | `stardew_mod_diagnostics` | Exact active role and staged mod ID; bounded isolated log |
| Refresh selected CP patch JSON | `project review cp-refresh` | No | Owned single root CP 2.9.1 target; explicit source/files and Data observation |
| Canonical structured Data | `project review data` | Data tools | Active single review |
| Maps, textures, audio, observed mod assets | Corresponding `project review` subcommands | No | Active single review |
| Map / viewport screenshots | Quoted review console command | Screenshot tool | Active selected role; map needs loaded world; viewport can diagnose title/loading state through CLI |
| Button, cursor, wheel input | Quoted review console command | Input tools | Active selected role; MCP requires `--allow-input`; mouse requires virtual cursor, wheel also a menu |
| Fixture status / navigation | Quoted review console command | Fixture tools | Owned disposable world; MCP requires `--allow-fixture-actions`; any role |
| Fixture building / animal ensure | Quoted review console command | Fixture tools | Owned disposable world; single or host only; MCP fixture opt-in |
| Fixture object ensure / clear | Quoted review console command | No | Owned disposable world; single or host only |
| Fixture save | No standalone fixture-save console command | `stardew_fixture_save` | Owned disposable world; single or host; MCP fixture opt-in |

`network-2` means exactly one local host and one farmhand. It does not establish general multiplayer compatibility. MCP role selection is fixed at server startup. Narrow CLI title/loading exceptions do not bypass MCP's own readiness checks.

## Read a result

- Exit `0`: the requested operation succeeded. Check its specific result fields before making a broader claim.
- Exit `2`: command syntax or arguments are invalid; use the matching `--help`.
- Exit `3`: a controlled discovery, build, runtime, or ownership failure; read `problems`, warnings, and result-named logs.
- `commandWritten=true`: a console line was delivered, not proof of the command's effect.
- Build identity describes the controlled staged file set, not an in-memory DLL measurement.

The [live guide](live-review.md#finish-or-test-persistence) distinguishes successful cleanup, retained state for restart, and warnings.

# Contributing to SDVKit

Read [AGENTS.md](AGENTS.md). Keep changes tied to a concrete mod-development workflow, and reuse the existing CLI/lab paths. The [GitHub roadmap](https://github.com/Nana1873/SDVKit/issues/84) owns future scope; do not maintain a second local roadmap.

Use English for code, CLI text, documentation, issues, and pull requests. Chat and progress follow the contributor's preferred language.

## Build and check

Windows and the .NET 8 SDK selected by `global.json` are required. Run from the repository root:

```powershell
dotnet restore SDVKit.sln
dotnet format SDVKit.sln --verify-no-changes --no-restore
dotnet build SDVKit.sln -c Release --no-restore
dotnet test SDVKit.sln -c Release --no-build
git diff --check
```

The solution builds the CLI and game-free tests. Tests link selected AlwaysOn source, but do **not** compile the full `SDVKIT_GAME_AVAILABLE` implementation. A game-bound change also needs an AlwaysOn build against the discovered local installation and the affected real-game checks in the [release matrix](docs/releasing.md#select-the-checks).

Use `.sdvkit/` for generated reports, fixtures, screenshots, logs, and packages. Ordinary .NET `bin/obj` outputs are ignored. The portable verifier deliberately extracts to a fresh external temporary directory to prove checkout independence; that retained directory is its explicit exception and must never be a normal Saves/Mods directory.

## Documentation changes

- Keep the root README focused on installation and one example for each product pillar.
- Put command behavior in the relevant [guide/reference](docs/README.md); agent skills link to it and retain only workflow-specific decisions.
- Update the [capability matrix](docs/README.md#capability-matrix) when a supported target, topology, role, or opt-in changes.
- Add user-visible changes to `CHANGELOG.md` under `Unreleased`. Explain the trigger and resulting behavior; implementation/CI evidence belongs in the PR or release issue.
- Check relative links and anchors, run changed executable examples where practical, and inspect root/subcommand help. Distinguish live examples inspected statically from those actually run.

The existing CI runs restore, formatting, build, the complete tests, packaging, and portable verification. It does not launch Stardew. Offline checks may run independently; live work requires one known owner of the selected lab and verified teardown before handoff.

## Release

Follow the [release procedure](docs/releasing.md). It defines artifact identity, focused live acceptance, retry rules, and publication verification. Do not substitute build success for actual in-game evidence.

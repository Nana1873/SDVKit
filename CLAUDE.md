@AGENTS.md

# Working on SDVKit itself

This repository is SDVKit's source. To use SDVKit for mod development, set up a separate lab root with `sdvkit lab init`; see [agent/SDVKIT.md](agent/SDVKIT.md).

- Build, format and test: `dotnet build SDVKit.sln -c Release`, `dotnet format SDVKit.sln --verify-no-changes`, `dotnet test SDVKit.sln -c Release`.
- Game-bound GameSide changes also need `.\scripts\build-gameside.ps1`; CI cannot compile them.
- `agent/` is the lab kit that `sdvkit lab init` writes into users' lab roots. Its files are templates with `{{version}}`, `{{docs}}` and `{{exe}}` placeholders, not instructions for this repository.
- Contribution rules and the checks before merge: [CONTRIBUTING.md](CONTRIBUTING.md).

---
name: sdv-project-smoke
description: Run SDVKit's automated end-to-end smoke test for one standalone SMAPI C# mod - build it, load it with SMAPI in the isolated lab, run bounded game ticks, and clean up. Use when asked to smoke-test a C# mod; not for content packs or for checking specific behavior.
---

# Smoke-test a C# mod

1. Discovery must find exactly one ready installation:

   ```powershell
   .\sdvkit.cmd doctor --json
   ```

   On `ambiguous` or `notFound`, stop and ask the user. Never pick an installation yourself or install anything.
2. The project must be one standalone C# mod:

   ```powershell
   .\sdvkit.cmd project inspect .\workspaces\MyMod --json
   ```

   Continue only when `kind` is `smapiMod`, `problems` is empty, and there is exactly one manifest with `EntryDll` and one `.csproj`. Content packs are not supported; use `sdv-live-review` instead.
3. Check that no review or smoke is running in this lab (`project review status --json`). Then run one topology:

   ```powershell
   .\sdvkit.cmd project smoke .\workspaces\MyMod --topology single --json
   ```

   Use `--topology network-2` only when the user asked about multiplayer.
4. Read the JSON result: build, SMAPI load of the exact mod version, the bounded tick run, and the final cleanup. Report each part separately, and quote the first failing part with its problem code.

A passing smoke proves that the mod builds, loads and survives a short run. It does not prove that its features work; use `sdv-live-review` for that.

Reference: {{docs}}/live-review.md

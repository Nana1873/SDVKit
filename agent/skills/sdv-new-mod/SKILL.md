---
name: sdv-new-mod
description: Create a new Stardew Valley mod with SDVKit, either a SMAPI C# mod or a Content Patcher content pack, in this lab's workspaces folder, and confirm it passes the offline check. Use when the user wants to start a mod.
---

# Create a mod

1. Agree on the mod's name, author, a unique ID in the form `Author.ModName`, and a one-line description. Pick the kind:
   - `smapi-mod` for C# code (events, console commands, Harmony, custom logic).
   - `content-pack` for Content Patcher edits (data, images, maps, dialogue) without code.
2. Put it in `workspaces/<ModName>/` unless the user chose another path. Never overwrite an existing folder.
3. Create and check it:

   ```powershell
   .\sdvkit.cmd project create smapi-mod .\workspaces\MyMod --name "My Mod" --author "Me" --unique-id "Me.MyMod" --description "What it does." --json
   .\sdvkit.cmd project inspect .\workspaces\MyMod --json
   .\sdvkit.cmd project check .\workspaces\MyMod --json
   ```

   For a Content Patcher pack, use `content-pack` and create it in a `pack` subfolder, for example `.\workspaces\MyPack\pack`. The review and the package then select that clean pack root, while the repository's own files stay one level up.
4. Offer to make the folder its own git repository. The lab's `.gitignore` already excludes `.sdvkit/`; a mod repository should ignore its own `.sdvkit/`, `bin/` and `obj/`.
5. Report what was created and that `project check` passed. Nothing has run in the game yet.

Next: `sdv-smapi-csharp` or `sdv-content-patcher` to develop it, `sdv-build-package` to release it.

Reference: {{docs}}/toolkit.md

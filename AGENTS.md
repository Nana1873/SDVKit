# SDVKit agent workflow

1. Implement the smallest complete change for a concrete Stardew mod-development workflow.
2. Reuse in this order: existing SDVKit behavior, supported SMAPI/.NET behavior, a thin adapter around an established tool, then new code.
3. Keep normal saves and the normal or mod-manager-owned `Mods` directory outside automatic development and test operations.
4. Keep generated builds, profiles, logs, fixtures, screenshots, reports, and backups below the project's ignored `.sdvkit/` directory.
5. Never commit game binaries, proprietary assets, personal saves, secrets, or absolute machine-local paths.
6. Distinguish build success, automated tests, and verified in-game behavior. Claim only the level actually proven.
7. Do not add generic frameworks, duplicate state machines, new runtime projects, protocol layers, or evidence schemas without a demonstrated current need.

The public product has two equal pillars: a modding toolkit and an isolated live test lab. Keep both understandable from the root README and CLI help.

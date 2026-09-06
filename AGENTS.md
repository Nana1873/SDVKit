# SDVKit agent workflow

1. Implement the smallest complete change for a concrete Stardew mod-development workflow.
2. Reuse in this order: existing SDVKit behavior, supported SMAPI/.NET behavior, a thin adapter around an established tool, then new code.
3. Keep normal saves and the normal or mod-manager-owned `Mods` directory outside automatic development and test operations. Explicitly selected real saves may be imported as isolated copies below `.sdvkit/`; never modify the source save automatically.
4. Keep generated builds, profiles, logs, fixtures, screenshots, reports, and backups below the project's ignored `.sdvkit/` directory. The portable archive verifier is the explicit exception: it retains a fresh external temporary extraction to prove checkout independence, never a normal Saves/Mods directory.
5. Never commit game binaries, proprietary assets, personal saves, secrets, or absolute machine-local paths.
6. Distinguish build success, automated tests, and verified in-game behavior. Claim only the level actually proven.
7. Do not add generic frameworks, duplicate state machines, new runtime projects, protocol layers, or evidence schemas without a demonstrated current need.
8. Automated in-game UI mouse input must use SDVKit's existing process-local virtual cursor after exact review ownership and role verification, and must fail closed. Never use global `SendInput`, physical cursor movement, window focus changes, or generic computer-use automation for review mouse input.
9. Use English for code, CLI text, documentation, issues, and pull requests. Use the contributor's preferred language for chat and progress updates.
10. Implementation and offline checks may run in parallel. Before live tests, identify the intended lab context and its owner; if another task owns it, wait for that task's verified stop/reset before taking over. Use existing task/status and ownership checks, and never stop another task's game or modify its worktree.
11. Capture deferred user ideas in this repository's GitHub issues after checking for an existing match, and return the issue link. Capture does not authorize implementation; do not create a parallel local roadmap.
12. Continue already authorized steps without repeated confirmation. Ask only if authorization for a changed scope or target is unclear; explain actual tool/policy blocks without bypassing them.
13. When creating a new user mod without a chosen destination, use the recommended `workspaces/<ModName>/` under the user's lab root, alongside `.sdvkit/`. Respect explicit paths and existing project locations. Keep each mod's source/repository boundary separate and run live commands from the shared lab root with an explicit mod path; see [mod workspaces](docs/toolkit.md#choose-a-mod-workspace).

The public product has two equal pillars: a modding toolkit and an isolated live test lab. Keep both understandable from the root README and CLI help.

Use `docs/README.md` to find task guides and command references; skills should link to those contracts rather than duplicate them. Maintain user-visible changes in `CHANGELOG.md`. For releases, use `docs/releasing.md`: choose affected live gates before starting, reuse only fully passing evidence for the same artifact and relevant environment, and keep exact ownership and final cleanup mandatory.

## Coordination and worker ownership

- The coordinator clarifies objectives, scope, priorities, and consequential cross-project decisions. Delegate directly through existing task communication and handle replies and follow-ups there; never ask the user to relay copy/paste worker prompts.
- Astra Low workers own bounded work end to end: investigation, implementation, tests, routine fixes, and normal implementation decisions through a reviewable result. Pass the goal, relevant existing findings and file references, boundaries, and observable success criteria. Avoid duplicate full coordinator investigations, long history dumps, and micro-step handoffs.
- Group cohesive small changes and reuse suitable tasks. Create extra tasks or subagents only for a concrete benefit; do not start speculative work. Escalate only real blockers, conflicting requirements, material scope expansion, or consequential decisions. If diagnosis stalls, request targeted higher reasoning and reconsider the evidence instead of repeating failed approaches.
- Completion reports contain concise changes, file/diff references, exact checks and results, and open risks; provide full logs on demand. The coordinator reviews in proportion to risk and states a concrete reason for additional investigation or review. Distinguish worker-reported checks from checks personally verified by the coordinator.
- Preserve all approval, isolation, and mandatory checks in [Contributing](CONTRIBUTING.md#build-and-check) and the [release procedure](docs/releasing.md). Reuse valid passing evidence for the same artifact and relevant environment under those contracts; do not add restarts or repeat checks without a concrete reason.
- Update the user for material progress, decisions, blockers, and completion, rather than relaying internal messages. Claim time or cost savings only when measured.

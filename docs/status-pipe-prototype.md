# Experimental status pipe

This opt-in prototype tests whether active lab status can avoid repeated Windows file replacement. The original sporadic native rename error 5 remains unexplained; neither moving Temp nor the bounded rename retry trial resolved it. This is not the default status transport.

From the explicitly owned lab directory, set the process-local environment variable before a new launch:

```powershell
$env:SDVKIT_EXPERIMENTAL_STATUS_PIPE = '1'
& $sdvkit project review start $modPath --topology single --test-save --json
Remove-Item Env:SDVKIT_EXPERIMENTAL_STATUS_PIPE
& $sdvkit project review status --topology single --json
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
```

Prepare the owned disposable baseline first if absent, following [live review](live-review.md#use-the-disposable-world). For network-2, select the same switch before launch and use the normal host/farmhand workflow. Do not change global or persisted Windows environment variables.

The selected transport is recorded in the launch state. Subsequent CLI and MCP clients use that recorded selection even after the environment variable is removed. Existing file-based labs remain file-based. Stop an active lab with its matching version before changing packages; older clients do not understand a running pipe-based lab.

AlwaysOn serializes a complete bounded snapshot on the game thread and publishes immutable bytes in memory. Background pipe handlers serve that snapshot without reading game objects or requiring a client to keep game updates running. Clients connect to the local machine, verify the pipe server process, and apply the existing identity, size, freshness, fixture and role validation. The managed pipe restricts access to the current user; this prototype does not claim that Windows rejects all remote pipe connections by configuration.

One persistent listener serves requests sequentially. Each framed snapshot has a length limit and a receive acknowledgement. Server response handling and client connection/read handling each request a one-second total timeout. Concurrent callers can wait for the listener, and a busy or stalled connection may make another read unavailable. These are cancellation deadlines, not guaranteed wall-clock limits under arbitrary scheduling delays; they do not stop the game thread from publishing newer snapshots.

Active status does not write or replace `always-on-status.json`. A missing pipe is not permission to use an old active file. An unresponsive game retains an old observation time and must still become stale.

Controlled shutdown still writes one terminal `exiting` or `restoreFailed` receipt at the owned status path. Existing stop/recovery checks combine that receipt with exact process exit. A closed pipe alone cannot establish clean shutdown, and a missing, invalid or unwritable receipt remains a reported failure. This keeps terminal disk I/O and its possible errors explicit.

Acceptance must distinguish legacy file tests, new pipe tests, game-backed compilation and actual single/host/farmhand behavior. Keep the original strict file-concurrency test and its historical failures; passing a different transport does not fix or explain that failure. Verify concurrent reads, stale and disconnected endpoints, exact process binding, bounded clients, terminal failures, restart and final owned cleanup before considering a default change.

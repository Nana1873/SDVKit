# Status rename and reader investigation (#136, #143)

Investigation date: 2026-10-11. Source base: `7513582491ae4671cc56e516ee1e975b8d74154f`.

## Findings

The cause of the intermittent failure remains **unresolved**. This investigation reproduced #136 on unchanged production code, narrowed the leading ordinary-handle-sharing hypothesis, and demonstrated a separate mechanism that can return a null reader timestamp without a writer failure. It does not identify Defender, Windows Search, another process, or an SDVKit defect as the cause. No production fix, retry, fallback, test weakening, or settings change is proposed as a repair.

1. A normal source reader omitting `FILE_SHARE_DELETE` does **not** reproduce the observed sequence of a successful source `DELETE` open followed by rename error 5. In the controlled cross-process cases it causes error **32** at the incompatible open. Metadata-only source handles do not produce the denial either.
2. The unchanged complete suite reproduced rename error **5** under a directory that the Windows Search crawl-scope API reports as **not included**. This weakens a simple indexed-directory explanation; it does not rule out filesystem filters or establish historical indexing state.
3. A destination opener that permits delete sharing but denies read sharing makes the real reader return `invalid`/null with error **32**, while the real writer can still publish successfully. This is a deterministic availability mechanism consistent with the shape of #143, not evidence that it caused either historical occurrence.

## Scope and retained history

Read both issue bodies and all comments (19 on #136, four on #143), the native publisher, status writer/reader, both strict stress tests and their existing failure diagnostics. Read-only inspection also recovered the earlier #250 recurrence.

The local #250 suite at source commit `9acf967e9a376c688a951c8e17824f59c9549cd8` had 2,518 passes, one failure and zero skips. The scanner writer failed at tick 207 after 206 completed writes; all 2,500 scans completed without an observer exception. Complete old tick 206 and new tick 207 snapshots were retained, with subsequent `Archive` attributes and an observed thread-local `0xC0000022` mapping to saved Win32 error 5. The original test-binary hash was not retained; subsequent builds reused the output directory, so present-day binaries cannot establish that historical binary identity. Seventeen original evidence files were copied and verified against before/after SHA-256 inventories without modifying the originals.

Prior issue evidence already limits several proposed explanations:

| Earlier experiment | Result and limit |
| --- | --- |
| Writer alone, enumeration, attributes, full scanner | Four 750-write arms passed; scanning arms performed 2,500 scans. Nonreproduction only. |
| Targeted same-volume Temp comparison | Eight fixed ABBA-ABBA runs, four in each location, all passed. |
| Identical-artifact full-suite Temp comparison | Default Temp: 1,784/1,784; owned alternative outside Temp: 1,783/1,784, native error 5 after 550 writes. Relocation did not prevent failure. |
| Four bounded native rename attempts | 1,791/1,792; error 5 persisted after all four attempts. This rejected mitigation is not included here. |
| Five ordinary data-mapping probes | All succeeded, including source and destination mappings. |
| Readable narrow Procmon trace | 43,180 filesystem events; suite 1,866/1,866. Only intentional ReadOnly denials, no sporadic failure attribution. |
| Historical #143 null reports | Two local full-suite failures with no reported writer exception. They must not be relabeled as writer error 5. |

See the complete [#136 history](https://github.com/Nana1873/SDVKit/issues/136) and [#143 history](https://github.com/Nana1873/SDVKit/issues/143). Earlier failed runs remain failed evidence.

## New unchanged-suite recurrence

One Release build and one format verification passed. One complete ordinary-parallel test suite then ran with process-local `SDVKIT_RUN_STATUS_FILE_STRESS=1`:

```powershell
dotnet build SDVKit.sln -c Release
dotnet format SDVKit.sln --verify-no-changes --no-restore
dotnet test SDVKit.sln -c Release --no-build --logger "trx;LogFileName=suite-01.trx" --results-directory .sdvkit/investigation-136/suite-01
```

TEMP/TMP, CLI home and package/HTTP caches were confined to the owned worktree. An empty `Directory.Build.props` at the temporary root prevented synthetic test projects from inheriting repository analyzer settings, an already documented historical confound. Production code, assertions, workloads and test parallelism were unchanged. The suite was not repeated.

- **2,525 passed / 1 failed / 0 skipped / 2,526 total**; launcher wall time 64.0 seconds.
- `EnsureDirectoriesToleratesAtomicStatusReplacementTempFiles` failed (TRX duration 7.767 seconds). Completed writes: **71**; next tick: **72**; completed scans: **2,500**; no scanner exception.
- Failure was at `WindowsStatusFile.Publish`'s `SetFileInformationByHandle`, after the completed source opened with `DELETE` access. Saved Win32 result: **5**.
- Failure-only capture at `2026-10-11T13:59:12.6020558Z` retained complete 267-byte old tick 71 and new tick 72 JSON, before publisher cleanup. Both subsequent attribute observations were `Archive`.
- Immediate thread-local last-native-status observation: **`0xC0000022`**, mapping to 5. This is **not** an independent record of the originating syscall or a competing actor.
- `ConcurrentReaderSeesCompleteExactSnapshotsWithContractFreshness` **passed** (500 writes / 1,000 reads; TRX duration 1.813 seconds). The original #143 failure did not recur.

All test-output binary hashes matched before/after the suite. Selected SHA-256 identities:

| Binary | SHA-256 |
| --- | --- |
| `SdvKit.Tests.dll` | `C0A93983792EFCEE6B201E9DE04DCCB171FF6B7E87548790717578E550291E18` |
| `sdvkit.dll` | `04A73A1191D4510A8892CE92448E9D1FE13A70F53DB3B7B1D9E33DD2E87678DE` |
| `SdvKit.Shared.dll` | `362A6C10CBECAF2B313A9B81811C2BADAEC80DEBCEAC3356421A6BD8BDE40DBE` |

The environment was Windows x64 build 26200, .NET runtime 8.0.25, with an unelevated test identity. Defender real-time protection and Windows Search were reported running; no settings were changed. `ISearchCrawlScopeManager.IncludedInCrawlScope` returned false for the exact suite Temp root and the native-probe root, as well as the parent owned root and default Temp. No new outside-worktree location arm was run: the existing A/B already reproduced outside default Temp, and this task restricts test writes to the worktree.

## Native source/destination handle matrix

An isolated console prototype used the production source access, extended paths, rename structure/allocation, `SetFileInformationByHandle` class 22 and replace/POSIX flags 3. A separate child process held each competing handle, reported its open result and waited until after the rename attempt. Each case used fresh owned files. No external process was injected into, stopped or reconfigured.

The predeclared matrix contains 128 combinations:

- source or destination competitor;
- competitor opened before or after the publisher handle;
- publisher access `DELETE` or `GENERIC_READ` without DELETE;
- publisher share `Read|Delete` or `Read`;
- competitor access `GENERIC_READ`, `DELETE`, zero or `FILE_READ_ATTRIBUTES`;
- competitor share `ReadWrite|Delete` or `ReadWrite`.

Seven controls cover baseline, no publisher DELETE, no publisher share-delete, ReadOnly source/destination, and read-only source/destination data mappings. These are intentional experiments, not suggested production changes.

| Matrix-01 stage | All outcomes |
| --- | --- |
| Cases | 135 completed once; 15.58 seconds |
| Publisher open | 128 succeeded; seven error 32 |
| Competing process open (128 cases) | 121 succeeded; seven error 32 |
| Rename (128 attempts) | 56 succeeded; 64 error 5; eight error 32 |
| All rename error-5 cases | 62 matrix rows without DELETE access, the no-DELETE control, and the ReadOnly-destination control |
| All rename error-32 cases | Eight destination content-read/DELETE blockers without delete sharing |

With production DELETE access:

| Competing ordinary handle | Observation |
| --- | --- |
| Source content read without delete sharing, opened first | Publisher source-open fails 32; rename is not attempted. |
| Source content read without delete sharing, opened after publisher | Competing open fails 32; rename succeeds. |
| Source metadata-only open, including no delete sharing | Coexists; rename succeeds. |
| Destination content-read/DELETE handle without delete sharing | Rename fails 32. |
| Compatible destination reader | Replacement succeeds. |
| ReadOnly destination | Rename fails 5; deliberate calibration. |
| ReadOnly source or ordinary read-only source/destination data mapping | Rename succeeds. |

Removing `FILE_SHARE_DELETE` from the publisher handle adds incompatibility with competing DELETE-access handles. Requesting `GENERIC_READ` without DELETE directly produces error 5. Neither variation is an evidenced fix for the historical failure.

The first matrix has a diagnostic limitation: the child's first `RtlGetLastNtStatus` P/Invoke apparently disturbed the thread-local value, yielding `0xC0000135` for differing saved Win32 outcomes. Those child status observations are not attributed to `CreateFile`. Saved Win32 errors remain valid. A separate diagnostic-only version warms the import before measurement; its targeted follow-up and bounded loop results are recorded below. Even warmed last-status observations are not syscall tracing.

### Diagnostic follow-up and bounded standalone loops

`warmcheck-01` ran five predeclared cases after warming the native-status import: four publisher opens succeeded, one failed with 32; four renames were attempted, two succeeded and two deliberate controls failed with 5. The incompatible child open now observed `0xC0000043` alongside saved Win32 32. No case was retried.

This follow-up exposed a probe-only snapshot problem: its `File.ReadAllText` diagnostic reader omitted delete sharing and conflicted with the still-open publisher DELETE handle. The original diagnostic read failures are retained. Before the planned loops, native build-04 changed only that diagnostic reader to `ReadWrite|Delete`; it did not change the measured write/open/rename sequence. No warmcheck repeat followed.

`loops-01` then executed the two originally declared arms, each capped at 2,000 operations, stopping the entire phase on its first native failure. Each operation wrote and closed a fresh JSON source, opened it with DELETE access, then used the same rename buffer/flags as production. There was no toolkit reader, directory scanner or competing helper child. Per-operation JSONL logging adds its own filesystem activity; this was not an otherwise idle-machine benchmark.

| Loop arm | Attempted | Succeeded | Failed | Deliberately unattempted after stop |
| --- | --- | --- | --- | --- |
| Publisher share `Read|Delete` (production sharing) | 2,000 | 2,000 | 0 | 0 |
| Publisher share `Read` | 1,539 | 1,538 | 1 | 461 |
| Total | 3,539 | 3,538 | 1 | 461 |

The second arm failed at zero-based iteration **1,538**, saving rename Win32 **5** and a warmed thread-local **`0xC0000022`** observation after a successful source open. Open time was 46 microseconds and rename-call measurement 2.9894 milliseconds. These are instrumented call measurements, not filter/actor timing.

Before closing the failed source handle, successful `NtQueryInformationFile` queries reported granted access **`0x00110080`** (DELETE present), `DeletePending=false`, one link and a 66-byte file. Failure-only snapshots retained source tick 1,538 and destination tick 1,537, both with subsequently observed `Archive` attributes. Both files remain retained. No native operation was retried.

This is a smaller reproduction of the **failure shape**, on a deliberately different publisher sharing arm. It shows that removing publisher delete sharing is not a reliable repair and that this shape can occur without the toolkit's concurrent scanner/reader. It does **not** prove that this variant and the original production-sharing failure have the same underlying cause. The production-sharing loop's 2,000 passes do not establish immunity or a comparative failure rate.

The harness exited 0 to indicate collection completed, and its generic matrix summary does not count loop operations. The loop iteration/failure/completion records above are authoritative: **this loop experiment contains one failure**, not an all-pass run. Total launcher wall time was 5.46 seconds. JSONL SHA-256: `C7AEF7CF11DBE60699D1985B824C903ADC25FAEDF24FBA305FA40C431B797A90`.

A minimal reproduction protocol is the fixed sequence below in a fresh owned directory; use the unchanged production rename structure, class and flags. The two share modes are separate experimental arms, not retries. The observed failure is intermittent, so future passes would be nonreproduction only.

```text
for sourceShare in [Read|Delete, Read]:
    at most 2000 times:
        write complete JSON to the fixed sibling temporary filename; close writer
        CreateFileW(temporary, DELETE, sourceShare, OPEN_EXISTING)
        SetFileInformationByHandle(handle, FileRenameInfoEx=22,
                                   ReplaceIfExists|PosixSemantics=3, destination)
        retain every open/rename result and timing
        on any unexpected failure: record state, stop both arms, do not retry
        close source handle
```

## Reader availability matrix

The real compiled reader/writer assemblies above were invoked through a console reflection harness. For each of three cases, the harness published tick 1, held an ordinary destination read handle, read the current snapshot, attempted tick 2 while the blocker remained open, read again, released the blocker, and read once more. A baseline read precedes each blocker. First-chance exceptions were collected around the selected synchronous reader invocation.

| Destination sharing | Read while blocker held | Publication while held | Subsequent read |
| --- | --- | --- | --- |
| `Read` | `active`, tick 1, timestamp present | Rename denied with `0x80070020` (32) | Complete tick 1 remains readable. |
| `Delete` only | `invalid`, null timestamp; original `IOException` `0x80070020` | Succeeds | `active`, tick 2, timestamp present even while old blocker is held. |
| `Read|Delete` | `active`, tick 1, timestamp present | Succeeds | `active`, tick 2, timestamp present. |

Corrected harness execution: **3/3 cases**, six publications (five successful, one expected denial), 12 reads (11 valid, one controlled null timestamp). These are deterministic sharing controls, not reproduction of an unknown actor.

`GameSideStatusReader.Read` returns a null top-level timestamp only for `pending` (`File.Exists` false, including inaccessible cases), `invalid` (bad length or caught IO/security/access/JSON failure), or `mismatch` (invalid marker/envelope). A `stale` report retains its timestamp. The production reader already shares `ReadWrite|Delete`; its normal reads are compatible with the publisher and each other. Omitting delete sharing alone does not deny read access, so it does not explain the historical null timestamp without an additional mechanism.

The historical #143 reports do not identify which of those paths executed. No reader fallback, cache or retry was added.

## Complete run accounting and limitations

| Run | Result |
| --- | --- |
| Solution build-01 | Passed, zero warnings/errors. |
| Solution format-01 | Passed. |
| Solution suite-01 | Failed: 2,525/2,526 passed, zero skipped; #136 recurrence above. No suite repeat. |
| Native build-01 | Failed six prototype analyzer diagnostics. Retained unchanged. |
| Native build-02 | Passed after invariant parsing/formatting and cleanup corrections. |
| Native matrix-01 | Completed all 135 declared cases; outcomes above include every denial. |
| Native build-03 | Passed after diagnostic import warm-up and failure-only handle metadata addition. |
| Native warmcheck-01 | Five declared controls completed; two rename successes, two expected rename-5 denials, one source-open-32 denial. Snapshot diagnostic self-conflicts retained. |
| Native build-04 | Passed after correcting only diagnostic snapshot sharing. |
| Native loops-01 | 3,538 successes / one unexpected rename-5 failure / 3,539 attempts; remaining 461 operations not attempted under the declared stop rule. No loop repeat. |
| Reader build-01 | Passed. |
| Reader execution-01 | Invalid harness: 0/3 checks; all 12 reader calls failed assembly loading with `FileLoadException` `0x80131621`. Six writer calls still occurred (five successes, one expected denial). No reader conclusion is drawn from this run. |
| Reader build-02 | Passed with `MSB3277` dependency warning; not executed. |
| Reader build-03 | Passed without warnings after aligning the harness JSON dependency. |
| Reader execution-02 | Valid 3/3 matrix; counts above. Earlier harness failure remains retained. |

The native matrix finished before the full suite; the valid reader execution and bounded native loops ran afterward. Reader build-02 overlapped the final portion of the suite, after the recorded native failure; this was not an otherwise idle-machine performance experiment. No test was rerun to replace a failed result.

Read-only environment-query preparation also had two limitations: the `Search.Manager` ProgID was unavailable, and the sandbox initially hid Search availability. A later explicit SDK-interface COM query succeeded against an already running service, with unchanged process identity. Neither initial failure was a test result or evidence that Search was absent.

Private raw stdout/stderr/TRX, all helper versions and ledgers, complete matrix events, native/post-worker snapshots and historical copies remain under ignored `.sdvkit/investigation-136/` and `.sdvkit/test-failures/`. The local paths and process identities in those private files are intentionally not committed. This report contains the full aggregate outcome counts; successful helper runs do not replace the failed suite. No game, live review, production code change or release acceptance occurred.

## Remaining information need and owner proposal

The missing evidence is a sporadic failing rename's originating native result together with overlapping exact-file access or filesystem-filter activity. The standalone loop now offers a smaller capture target, provided its modified sharing mode remains explicit. Ordinary `CreateFile` calibration does not simulate a kernel filter's special access or denial behavior. A current non-indexed scope and running security service cannot assign or exclude a causal actor.

If the owner authorizes an external diagnostic step, the smallest useful proposal is one bounded capture of the frozen standalone loop protocol:

1. Use a fresh owned probe root; filter **all actors** to that exact root and status/temp file operations. Include successful opens, share/access details, close, rename and stacks; do not exclude System.
2. Validate filter configuration and orderly trace finalization with one synthetic marker before the fixed two-arm batch. Preserve the original trace, source/binary hashes and native failure timestamp. Keep the batch capped at 2,000 operations per arm and stop on first failure. Bound the capture to 30 seconds and 1 GiB, with an orderly-stop window included in that budget. If no usable trace or no sporadic recurrence results, end the experiment and retain it as inconclusive.
3. Correlate the failing source/destination with overlapping handles and filter-stack results. A cause identified only in the modified-sharing arm must still be tested against the production-sharing contract before proposing a product fix. A process appearing nearby in time, an Operational log with no event, or a later attribute snapshot is insufficient attribution.

This capture is **not performed or authorized by this report**: loading/using an elevated tracing driver or system-wide tracing facility can affect state outside this worktree. No Defender exclusion, security-policy change, indexing change or global environment change is needed or proposed as a diagnostic shortcut. Do not change production behavior until a demonstrated mechanism and regression test justify it.

## Primary contracts

- [CreateFileW access and reciprocal sharing rules](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).
- [FILE_RENAME_INFORMATION flags and access requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information).
- [SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle).
- [Windows Search IncludedInCrawlScope](https://learn.microsoft.com/en-us/windows/win32/api/searchapi/nf-searchapi-isearchcrawlscopemanager-includedincrawlscope).
- [File.Exists false results on errors](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-8.0).

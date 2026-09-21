# Phase two — first milestone, 2026-09-20

Status: **in progress**, with a working plan → persistent queue → resumable download path.

## Implemented and verified

- Dependency catalog discovery across `ccm`, `sti`, `nocc`, and referenced dependency
  channels. Shared `win32` entries are considered for x64/ARM targets; payload selection
  still checks processor families and conditions against the requested target.
- Numeric version ordering, exact base/build constraints, topological download order,
  shared dependency deduplication, and explicit cycle/conflict/missing/ambiguous errors.
- A bounded condition parser for comparisons, boolean operators, parentheses, OS version,
  OS processor family, and install language. Unknown variables/syntax fail closed.
- Saved JSON plans include raw manifests, package metadata/validation URL, and package
  selection decisions. Only full payloads are selected.
- Atomic queue checkpoints, sequential execution, exclusive queue/item locks, disk
  preflight, paused/failed/completed states, and local SHA-256 completion receipts.
- Interrupted bytes resume only with a strong ETag. Range and total size must match.
  Missing/weak validators restart; a 200 response to a range request restarts; invalid
  range/ETag responses fail without publishing corrupt output. Retries are bounded.

Release build: **zero warnings and errors**. Automated suite: **79 passing tests**.
The suite includes connection loss, persisted cancellation/restart, changed validators,
missing validators, ignored ranges, invalid content ranges, 416 recovery, completed-file
tampering, queue locks/budgets, dependency graphs, condition parsing, and ARM64 selection.

## Live Adobe evidence

**Bridge plan:** `KBRG 16.0.7.36` on win64 / en_US / OS 10.0.22000 resolves
`ACR 18.6.0.25` from the shared win32 catalog. The plan includes four packages totaling
**2,395,437,854 bytes**. Its old-Windows support package is excluded because its condition
requires OS version below 10.0. This was metadata-only; the large application set was
not downloaded or installed.

**Persistent queue:** planned, downloaded, and reopened `VC14win64 2.0.0.2`,
`VCRedist14-64.zip`, **18,307,193 bytes**. A second run revalidated the completed file.

**Live interruption/resume:** interrupted that runtime archive after **1,054,082 bytes**.
The next request sent `Range: bytes=1054082-` and `If-Range` with the saved strong ETag.
Adobe returned **HTTP 206** with matching ETag and
`Content-Range: bytes 1054082-18307192/18307193`.

The resumed result's SHA-256 matched the independent phase-one full download:
`cae3f25eb564c9f6368cac7e672fc8e7e5aaa2541422f94148224d9e3c2f46c9`.
No archive was executed and no existing Adobe installation was modified.

Local evidence is under `.local/bridge-plan.json`, `.local/runtime-plan.json`,
`.local/runtime-queue/`, and `.local/live-resume/` (all excluded from Git).

## Remaining phase-two work

- Feature/module selection and broader product/condition compatibility. Unsupported
  feature metadata currently stops planning instead of silently omitting packages.
- Adobe validation metadata/signature verification. Local SHA-256 receipts detect
  changes relative to the download; they are not Adobe authenticity signatures.
- Delta package planning, including trusted installed-baseline validation; current
  plans deliberately use full packages.
- Additional ARM64 catalog coverage and hardware validation, plus a broader product
  compatibility matrix. No assumption of x64 emulation on ARM.
- Dependency constraint backtracking where compatible solutions need a different
  earlier selection; current conflicting selections fail explicitly.

HTTP resume reference: [RFC 9110, If-Range](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5).

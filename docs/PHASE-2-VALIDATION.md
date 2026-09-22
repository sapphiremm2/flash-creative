# Phase two: download planning and validation, 2026-09-22

Status: **in progress**, with a working plan â†’ persistent queue â†’ resumable download path.

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

Release build: **zero warnings and errors**. Automated suite: **137 passing tests**.
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

## Second milestone

- Adobe TYPE2 validation parsing checks the algorithm, version, unique segment indexes,
  exact file coverage, package key, and every segment's SHA-256. Both download paths verify
  before publication; reopening a completed queue fetches validation metadata again.
- New CLI plans require validation URLs. Legacy plans without them explicitly report
  `LocalSha256ReceiptOnly`. Queue items cannot override a plan's validation URL/key.
- Module references resolve package names, filenames, and aliases. Deferred/OnDemand
  packages require explicit selection; consent-requiring modules are never selected
  implicitly. Features use their `Name` field. Adobe's empty `{}` feature placeholder
  means no feature restriction; unknown nonempty objects fail.
- Bounded dependency search backtracks earlier choices to satisfy later exact constraints.
- Delta candidates and fallback reasons are retained. Full packages remain the only
  executable download plan; unverified installed versions do not authorize a delta.
- `verify-signature` uses Windows WinVerifyTrust with revocation checks and compares the
  embedded signer against an exact expected publisher. It does not execute files.

### Additional live evidence

A fresh CLI runtime plan and queue downloaded **18,307,193 bytes** and verified all
**nine segments** against Adobe's HTTPS TYPE2 endpoint. Reopening the completed queue
performed the checks again. SHA-256 remains
`cae3f25eb564c9f6368cac7e672fc8e7e5aaa2541422f94148224d9e3c2f46c9`.
Evidence: `.local/runtime-verified-plan.json` and `.local/runtime-verified-queue/`.

The archive's `1/VC_redist.x64.exe` was extracted without execution. Windows accepted
its embedded Microsoft Corporation signature (certificate thumbprint
`3F56A45111684D454E231CFDC4DA5C8D370F9816`). An expected-publisher mismatch was rejected;
changing a byte in a separate copy was rejected with `0x80096010` (bad digest).

Metadata-only plans, en_US / OS 10.0.26100:

| Product | Version | Target | Products | Packages | Bytes |
| --- | --- | --- | ---: | ---: | ---: |
| Bridge | 16.0.7.36 | win64 | 2 | 4 | 2,395,437,854 |
| After Effects | 26.5.0.89 | win64 | 6 | 19 | 3,407,465,362 |
| Photoshop | 27.10.0.26 | win64 | 13 | 41 | 4,708,861,075 |
| Photoshop | 27.10.0.26 | winarm64 | 12 | 37 | 4,091,309,836 |
| After Effects | 26.5.0.89 | winarm64 | 6 | 19 | 3,035,151,492 |

All selected packages in these plans expose validation URLs. The application payloads
were not downloaded or installed. Plans are under `.local/compat-*.json`.
After Effects defaults to its core package; explicitly selecting `AEFT-maxon` adds the
Cinema 4D package. Object Mask and the optional HEVC codec remain unselected.

Adobe's native ARM64 manifests use `ProcessorFamily=64-bit` and may retain `_x64` names;
their product platform and payload paths are `winarm64`. The planner treats this field
as bitness only in a matching native manifest. Shared win32 manifests do not grant ARM
compatibility to 64-bit payloads, and explicit `x64` families remain excluded on ARM.

## Broader x64 compatibility and delta inspection

The default target is Intel/AMD x64 (`win64`); ARM64 is additional coverage. The local
CLI and GitHub Windows tests run on x64. No ARM hardware execution is claimed.

Additional metadata-only plans, win64 / en_US / OS 10.0.26100 / individual deployment:

| Product code | Product version | Products | Packages | Bytes |
| --- | --- | ---: | ---: | ---: |
| ILST (Illustrator) | 30.8.1.1 | 8 | 28 | 2,311,611,322 |
| IDSN (InDesign) | 21.6.0.057 | 7 | 25 | 1,146,271,172 |
| PPRO (Premiere Pro) | 26.5.1.1 | 4 | 14 | 2,552,580,061 |
| AME (Media Encoder) | 26.5.0.85 | 2 | 2 | 1,688,912,702 |
| AUDT (Audition) | 26.5.0.82 | 2 | 2 | 567,105,313 |
| LRCC (Lightroom) | 9.5.202607280500 | 1 | 1 | 2,518,538,561 |
| LTRM (Lightroom Classic) | 15.5.1.202608131348 | 1 | 1 | 2,738,839,025 |
| DRWV (Dreamweaver) | 21.8.1.15907 | 1 | 2 | 472,852,103 |
| CHAR (Character Animator) | 26.0.0.50 | 2 | 2 | 1,483,823,819 |
| SBSTA (Substance 3D Sampler) | 6.0.3.10557 | 1 | 1 | 810,728,980 |
| SBSTD (Substance 3D Designer) | 16.0.6.11611 | 1 | 1 | 611,886,739 |
| SBSTP (Substance 3D Painter) | 12.1.5.5783 | 1 | 2 | 2,543,461,803 |

Lightroom Classic exposed `[IsEnterpriseDeployment]==true`. The planner now supports
this explicit input, defaulting to individual. With `--deployment enterprise`, its
ModelZoo package is added: two packages, **3,058,993,954 bytes**. Both CLI variants were
verified; no application payload was downloaded. Plans/results: `.local/coverage-*` and
`.local/lightroom-classic-{individual,enterprise}.json`.

`inspect-delta` now fetches and validates bounded diff metadata and explicit SHA-256
segment metadata. Optional local archive checks validate all bytes and require the
embedded diff JSON to match the fetched JSON. Tests cover traversal, alternate data
streams, reserved paths, duplicate properties/entries, unsupported actions, corrupted
archive bytes, and mismatched embedded metadata. Inspection never marks an installed
baseline verified and never applies a patch or changes the saved full-package plan.

The earlier Bridge metadata HTTP 403 did **not** recur. Live inspection succeeded:

| Product | Base to target package version | Instructions | Patch actions | Delta bytes | Segments |
| --- | --- | ---: | ---: | ---: | ---: |
| Bridge | 16.0.6.9 to 16.0.7.36 | 3,317 | 375 | 15,708,287 | 8 |
| Photoshop core | 27.9.1.1 to 27.10.0.26 | 10,922 | 581 | 50,501,864 | 25 |
| Premiere Pro | 26.5.0.99 to 26.5.1.1 | 21,234 | 915 | 226,008,823 | 108 |

Only the **Bridge delta archive** was downloaded, with all eight segments verified.
SHA-256: `2b6c2663dd26dc8a61a087293d24ad84212c000aebb56395668a746f4097642d`.
Its embedded diff JSON exactly matches the separately fetched metadata. Its ACE.dll
patch has a `BSDIFF40` header. This does not establish the old installed DLL's identity.
Metadata: `.local/*-delta-inspection.json`; archive evidence: `.local/bridge-delta/` and
`.local/bridge-delta-verified-inspection.json`. No patch or installer was executed.

Delta validation responses omit `packageHashKey`; parsing permits omission only when
no expected key was supplied. Full-package validation still rejects missing/mismatched
keys when the selected manifest supplied one.

## Read-only queue audit, 2026-09-22

`queue-audit` checks every checkpoint marked Completed against the saved plan, payload
size, receipt identity, receipt SHA-256, and checkpoint SHA-256. Online mode additionally
fetches fresh Adobe segment metadata when available. `--offline` makes no network requests
and reports only local receipt verification. The report separates `Complete` from
`AdobeVerified`; neither asserts that installation or delta application is ready.

Missing files are reported instead of downloaded. Failed, paused, or pending items are
reported as incomplete. Corruption and unavailable validation are reported per item,
without abandoning the rest of the report. Audit opens existing files read-only and
creates no lock files. Caller cancellation propagates without changing queue state.
Queue validation also rejects a copied item that changes its product, version, or package
name relative to the plan, even when its URL and bytes remain the same.

The existing **18,307,193-byte runtime queue** passed both modes. Offline reported
`Complete=true`, `AdobeVerified=false`; online reported both true after verifying all
nine Adobe segments. A before/after inventory and SHA-256 of every queue file confirmed
that no files were added, removed, or changed. Evidence inventory:
`.local/queue-audit-before.json` against `.local/runtime-verified-queue/`.

New tests cover mixed missing/corrupt/paused/valid items, network-free offline checks,
legacy receipts, fresh Adobe failures, missing queues, cancellation, and relabeled items.
The full suite has 137 passing tests; Release build has zero warnings or errors.

## Remaining phase-two work and dependencies

- **Delta execution is unavailable.** Safe selection needs a verified installed baseline
  and patch engine, both dependent on phase-three installation support. Delta metadata and archive
  verification now work, but the planner still chooses full packages and never accepts
  a claimed installed version as proof of the old files.
- **Detached Adobe signatures remain unverified.** The opaque `PackageValidation` field
  has no established trusted public key here. HTTPS segment checks and Windows embedded
  signatures are separate evidence and are not described as verifying this field.
- **ARM hardware and installation compatibility remain untested.** Native catalog and
  manifest planning is verified; the table does not demonstrate installation or launch.
- Broader Adobe products may expose additional condition/module schemas; unsupported
  metadata fails explicitly rather than silently producing a partial installation set.

Windows signature reference: [Microsoft WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust).
HTTP resume reference: [RFC 9110, If-Range](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5).

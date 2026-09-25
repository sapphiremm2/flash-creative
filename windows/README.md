# Flash Creative for Windows

Working catalog/manifest CLI, single-package downloader, and phase-two preview of
dependency planning and persistent resumable queues. This does **not** install
applications yet. See the phase-two limitations below before relying on a plan.
The upstream macOS application is preserved. The port remains under the repository's GPLv3 license.

## Supported machine targets

The default `win64` target is for ordinary **64-bit Intel and AMD Windows PCs**. The
CLI is built and tested on x64. `--platform winarm64` builds download plans from Adobe's
ARM catalog; it is additional coverage, not a requirement for using Flash Creative.
Native Windows x64 and ARM64 CI executes the core tests. The CLI still does not install
applications on either architecture.

## Build and test

Requires .NET SDK 8.0 or compatible newer SDK with .NET 8 targeting/runtime support.
From the repository root in PowerShell:

```powershell
dotnet build windows/AdobeDownloader.Windows.sln -c Release
dotnet test windows/AdobeDownloader.Windows.sln -c Release
$cli = 'windows/src/AdobeDownloader.Cli'
dotnet run --project $cli -c Release --no-build -- --help
```

The initial implementation uses the locally installed SDK. Upgrade the release
target before distribution as tracked in the five-phase roadmap.

## Find a product and inspect its packages

```powershell
dotnet run --project $cli -c Release --no-build -- catalog --product KBRG
dotnet run --project $cli -c Release --no-build -- catalog --product KBRG --json
# Use a ProductVersion returned by today's catalog; older builds may be withdrawn.
dotnet run --project $cli -c Release --no-build -- manifest --product KBRG --version 16.0.7.36
```

Defaults: `--platform win64`, `--channel ccm`, `--locale en_US`. `winarm64` can be
queried explicitly; native ARM64 CI verifies core execution, not Adobe app installation. Additional catalog
channels `sti` and `nocc` are supported. Legacy MSI/RIBS entries are shown with their
package type; the phase-one application-manifest flow only supports `hdPackage`.
`--version` must be an exact `ProductVersion`, not the catalog's marketing version.
Locale selects a catalog language set; `plan` also evaluates package language conditions.

## Download one selected package

```powershell
dotnet run --project $cli -c Release --no-build -- catalog --product VC14win64 --channel sti
dotnet run --project $cli -c Release --no-build -- manifest --product VC14win64 --version 2.0.0.2 --channel sti
dotnet run --project $cli -c Release --no-build -- download --product VC14win64 --version 2.0.0.2 --channel sti --package VCRedist14-64 --out .local/downloads --max-mib 32
```

The last example downloads an Adobe-hosted Windows runtime archive; it does not
execute it. The default download limit is 100 MiB; explicitly raise `--max-mib` for
larger packages. A JSON receipt on stdout gives the final path, byte count, and SHA-256.
Progress/error messages use stderr. Redirects must remain on Adobe HTTPS hosts.

Downloads use a temporary file, validate the expected byte count, and publish without
overwriting an existing file. Ctrl+C removes the partial file (exit code 130).
Network/validation errors return exit code 1. This `download` command intentionally
remains a simple single-package transfer. Use queues below for persistent resume/retry.

New CLI downloads and plans require Adobe's HTTPS validation endpoint. Before publishing
an archive, the downloader checks every segment against explicit TYPE2 (SHA-256)
metadata, including exact coverage and the manifest's opaque package key. Verification
also runs when reusing a completed queue file. Results report `AdobeHttpsSegmentSha256`.
The `packageHashKey` remains opaque; it is not the file's SHA-256.

`--sha256 <64 hex characters>` adds a comparison against a supplied digest. Older saved
plans without validation metadata remain readable and report `LocalSha256ReceiptOnly`;
regenerate their plan to require Adobe validation. HTTPS-served hashes are not detached
Adobe digital signatures. The opaque `PackageValidation` field is retained but unverified.

For an extracted executable, Windows signature verification is available separately:

```powershell
dotnet run --project $cli -c Release --no-build -- verify-signature --file .local/signature-check/VC_redist.x64.exe --publisher 'Microsoft Corporation'
```

Supply the exact expected signer name. This command verifies the embedded signature,
Windows trust/revocation policy, and publisher; it never executes the file. Trust checks
may need network access. It does not claim that ZIP archives have Authenticode signatures.

Manifest output retains dependencies, processor family, and condition expressions.
The single-package command downloads only the explicitly selected package; use `plan`
to evaluate supported conditions and dependencies. Raw Adobe manifest JSON can be saved with
`manifest ... --out <new-file.json>` for further inspection.

## Plan dependencies and use a persistent queue

```powershell
dotnet run --project $cli -c Release --no-build -- plan --product KBRG --version 16.0.7.36 --locale en_US --os-version 10.0.22000 --out bridge-plan.json
# Review the saved plan before creating a queue; planning downloads metadata only.
dotnet run --project $cli -c Release --no-build -- queue-create --plan bridge-plan.json --out bridge-queue
dotnet run --project $cli -c Release --no-build -- queue-status --queue bridge-queue
# Limit applies to the whole saved plan. This example permits up to 3,000 MiB.
dotnet run --project $cli -c Release --no-build -- queue-run --queue bridge-queue --max-mib 3000
```

`--os-version` defaults to the local Windows version. Specify it when building a
plan for another machine. `plan` preserves raw manifests and inclusion/exclusion
reasons. It resolves discovered Adobe dependency channels, exact base-version/build
constraints, and shared `win32` catalog entries; package processor/condition metadata
then controls payload selection. Dependencies precede their consumers. Missing,
ambiguous, cyclic, or unsatisfiable dependencies fail explicitly. Bounded backtracking
can select an older compatible dependency when a later product constrains its version.

Queue state is stored in `QUEUE_DIRECTORY/queue.json`; payloads and their receipt/resume
sidecars are under `packages/`. Queue execution is sequential and locked against concurrent
runs. Ctrl+C preserves partial downloads and marks the active item paused. Run the same
`queue-run` command to continue, including after a process crash. Completed files are
checked against their saved size/SHA-256 receipt and, when present, fresh Adobe segment
metadata before being reused. Queue status includes the verification method.

Resume uses a strong ETag with `Range`/`If-Range`, as specified in
[RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5). The client checks
the response range, ETag, and total size before appending. A full response restarts
the file; weak or missing ETags also restart rather than combine representations.
Transient failures get at most three attempts. Invalid range/size responses discard
the partial file. Disk preflight conservatively reserves the full remaining package
sizes, so it can require more free space than a resumed transfer ultimately needs.

## Audit an existing download set

```powershell
dotnet run --project $cli -c Release --no-build -- queue-audit --queue bridge-queue
# No network access: check file sizes, local receipts, and checkpoint hashes only.
dotnet run --project $cli -c Release --no-build -- queue-audit --queue bridge-queue --offline
```

Auditing never downloads replacements, resumes partials, repairs files, or changes queue
state. It reports every item as Verified, Incomplete, Missing, Invalid, or Unavailable.
Online audits fetch fresh Adobe segment metadata when the plan provides a validation
URL. Offline audits and legacy items without that URL report `LocalSha256ReceiptOnly`.
They never inherit an Adobe verification claim from a previous receipt.

`Complete` means every item passed the requested audit mode. `AdobeVerified` is true
only when every item passed fresh Adobe segment checks during this audit. An offline
success does not satisfy Adobe verification, even if `RequiresAdobeValidation` is true
in the saved plan. Exit code 0 means the requested mode passed; 1 means an incomplete or
failed audit, and 130 means user cancellation. Results describe the files at audit time,
not installation readiness. A concurrent writer may make an audit unavailable.

## Optional modules and features

Inspect `manifest` output for module IDs and feature names. Core packages are included;
Deferred/OnDemand modules and optional features require explicit selection. For example:

```powershell
# Use a version currently listed in the catalog.
dotnet run --project $cli -c Release --no-build -- plan --product AEFT --version 26.5.0.89 --modules AEFT-maxon --out after-effects-with-maxon.json
```

`--modules ID1,ID2` and `--features NAME1,NAME2` select root-product options. Use
`SAP:ID` or `SAP:NAME` for dependencies. Explicitly selecting a consent-requiring module
records the user's selection; the CLI never infers consent from a default. Unknown IDs,
missing references, and selected modules with no compatible packages fail planning.

## Individual and enterprise deployments

`plan` defaults to `--deployment individual`. Use `--deployment enterprise` only when
preparing that deployment type. The choice is saved as `IsEnterpriseDeployment` and
controls Adobe package conditions; it does not change licensing or grant an entitlement.
Lightroom Classic, for example, adds its ModelZoo package for enterprise deployments.

## Inspect delta metadata and archives

Regenerate a plan with the current CLI to retain its delta candidates, then select a
package and exact base package version from that plan:

```powershell
dotnet run --project $cli -c Release --no-build -- inspect-delta --plan bridge-plan.json --product KBRG --package AdobeBridge16.0-mul-x64 --base-version 16.0.6.9 --out delta-report.json
# If you already have the corresponding delta ZIP, add --archive path/to/delta.zip.
```

Inspection fetches Adobe's diff JSON and SHA-256 segment metadata, reports operation
counts, symbolic destination directories, extra fields, and full/delta sizes. It rejects
unsupported operations, duplicate properties, and unsafe relative paths. An optional
`--archive` is checked against Adobe's segments, then its embedded `<PackageName>_diff.json`
must exactly match the fetched metadata. Only that bounded metadata entry is decompressed
in memory; no payload is extracted or executed. Reports are written without overwriting.

`PayloadVerification` is null for metadata-only inspection. Even with a verified archive,
`InstalledBaselineVerified` and `CanApply` remain false: selecting a base version does
not prove the installed files match it. The command never converts a full-package plan
to a delta plan. Unknown extra fields are reported for installation research, not applied.

## Stage an archive-backed delta

Both archives must already be downloaded. Supply a plan for each exact version and a
new output directory. The command refreshes Adobe manifests and segment validation,
then reconstructs files beneath `payload/INSTALLDIR`, `payload/AdobeCommon`, etc.
These are symbolic staging folders, not actual system installation paths.

```powershell
dotnet run --project $cli -c Release --no-build -- stage-delta --plan bridge-plan.json --baseline-plan bridge-old-plan.json --product KBRG --package AdobeBridge16.0-mul-x64 --baseline-archive old.zip --archive delta.zip --out bridge-staged --max-mib 4096
```

Supported baseline formats are ZIP and ZIP-LZMA2. PATCH requires a matching EXISTS
record whose old-file size and SHA-256 must pass before BSDIFF40 application. Every
resulting file must match the target size and SHA-256. ADD/DELETE replacement pairs
and unchanged EXISTS files are supported. Unknown operation combinations and instruction
flags fail explicitly. Deletion omits a file from the new tree; it never deletes an
installed file. Attributes, registry operations, services, and installation commands
are not applied. Output size, patch memory, decoder dictionaries, and paths are bounded.

The destination must not exist. A private sibling workspace is removed on failure or
cancellation and renamed into place only after all file checks pass. The receipt says
`Installed=false`. Keep the resulting tree private until phase-three installation work
provides registration and rollback. Planners continue to choose full packages.

Compression uses SharpCompress 0.50.4 ([MIT license](SHARPCOMPRESS-LICENSE.txt)); the
BSDIFF40 reader implements the [documented format](https://www.daemonology.net/bsdiff/).

## Current limits

Phase two remains **in progress**. Plans use full packages and retain delta candidates
with an explicit fallback reason. Archive-backed staging is supported; updating an installed
application still depends on phase-three baseline inventory and transactional installation. The earlier
Bridge HTTP 403 did not recur: Bridge, Photoshop, and Premiere Pro delta metadata were
successfully inspected on 2026-09-21.

Live metadata planning covers Bridge, Photoshop, and After Effects on x64 and Photoshop
and After Effects on ARM64. Native `winarm64` manifests use `64-bit` to describe bitness,
sometimes retaining `_x64` names. Those names do not override the selected native platform.
Shared `win32` metadata alone does not authorize its 64-bit payloads on ARM, and explicit
x64 processor values are excluded on ARM. No ARM hardware installation has been tested.

Unknown condition variables or processor families stop planning. Plans are download
plans, not proof that a product can be installed or launched. Existing Adobe licensing
remains unchanged. See [phase-two evidence](../docs/PHASE-2-VALIDATION.md).

## Project map and roadmap

See `docs/WINDOWS-PORT-PLAN.md` and the phase validation documents.
Open `graphify-out/graph.html` for the local interactive map. Graph outputs and
downloaded payloads are ignored by Git.

```powershell
pwsh -File scripts/graphify.ps1 query 'CatalogClient ManifestClient' --budget 1200
pwsh -File scripts/graphify.ps1 update .
pwsh -File scripts/graphify.ps1 export html
```

AST updates are local. Use the Graphify skill for semantic updates to documentation.

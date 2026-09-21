# Flash Creative for Windows

Working catalog/manifest CLI, single-package downloader, and phase-two preview of
dependency planning and persistent resumable queues. This does **not** install
applications yet. See the phase-two limitations below before relying on a plan.
The upstream macOS application is preserved. The port remains under the repository's GPLv3 license.

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
queried explicitly but is not yet validated on ARM hardware. Additional catalog
channels `sti` and `nocc` are supported. Legacy MSI/RIBS entries are shown with their
package type; the phase-one application-manifest flow only supports `hdPackage`.
`--version` must be an exact `ProductVersion`, not the catalog's marketing version.
Locale selects a catalog language set; it does not evaluate package conditions yet.

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

SHA-256 is computed locally. Supply `--sha256 <64 hex characters>` to compare against
an independently trusted digest. Adobe's `packageHashKey` is retained as
`OpaqueHashKey`; it is **not** treated as a SHA-256 checksum. Adobe validation/signature
handling belongs to phase two. Downloaded packages are not claimed to be authenticated
beyond HTTPS unless an expected digest is supplied and matches.

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
ambiguous, cyclic, or conflicting dependencies fail explicitly.

Queue state is stored in `QUEUE_DIRECTORY/queue.json`; payloads and their receipt/resume
sidecars are under `packages/`. Queue execution is sequential and locked against concurrent
runs. Ctrl+C preserves partial downloads and marks the active item paused. Run the same
`queue-run` command to continue, including after a process crash. Completed files are
checked against their saved size/SHA-256 receipt before being reused.

Resume uses a strong ETag with `Range`/`If-Range`, as specified in
[RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5). The client checks
the response range, ETag, and total size before appending. A full response restarts
the file; weak or missing ETags also restart rather than combine representations.
Transient failures get at most three attempts. Invalid range/size responses discard
the partial file. Disk preflight conservatively reserves the full remaining package
sizes, so it can require more free space than a resumed transfer ultimately needs.

Phase two is **not complete**: plans currently use full payloads only. Feature/module
selection and Adobe validation/signature verification remain pending. Unknown condition
variables or processor families stop planning. ARM64 selection has fixture tests but
no hardware validation; x64 emulation on ARM is not inferred. Plans are download plans,
not proof that a product can be installed or launched. Existing Adobe licensing remains
unchanged. See [phase-two evidence](../docs/PHASE-2-VALIDATION.md).

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

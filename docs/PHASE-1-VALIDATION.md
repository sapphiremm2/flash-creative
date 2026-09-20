# Phase one validation — 2026-09-20

Status: complete for the scoped catalog/manifest/single-package milestone.
Installation and complete product dependency resolution remain future phases.

## Local validation

- .NET SDK 8.0.301, Windows x64.
- Release solution build: zero warnings, zero errors.
- xUnit: 40 tests passed, zero failed/skipped.
- Tests cover platform filtering, catalog dependencies/locales, legacy entries,
  XML DTD rejection, application identity matching, singleton/array package JSON,
  invalid package metadata, Adobe-only redirects, redirect limits, request headers,
  bounded downloads, missing Content-Length, size/hash mismatch, cancellation cleanup,
  filename traversal/reserved names, and existing-file protection.
- CLI size-limit failure returned exit code 1 with a readable error (no stack trace).

## Live Adobe checks

FFC v6 catalog request returned HTTP 200 for `platform=win64`, channels
`ccm`, `sti`, and `nocc`. The inspected response contained 620/79/203 product entries
respectively; these are time-dependent catalog entries, not counts of distinct apps.
The compiled CLI successfully listed 21 Bridge builds and three VC14win64 builds.

Application v3 metadata returned HTTP 200 for:

| Product | Exact version | Result |
| --- | --- | --- |
| Bridge (`KBRG`) | `16.0.7.36` | Parsed 678,228,382-byte core package and ACR dependency; application archive not downloaded |
| Microsoft VC runtime (`VC14win64`) | `2.0.0.2` | Parsed and downloaded `VCRedist14-64.zip` in full |

Downloaded runtime archive:

- Bytes: **18,307,193**, equal to Adobe manifest size.
- SHA-256: `cae3f25eb564c9f6368cac7e672fc8e7e5aaa2541422f94148224d9e3c2f46c9`.
- PowerShell `Get-FileHash` independently reproduced the downloader's hash.
- Local output: `.local/downloads/VCRedist14-64.zip` (ignored by Git).
- Raw manifests: `.local/vc14-manifest.json` and `.local/bridge-manifest.json`.
- Nothing was extracted or executed; existing Adobe installations were not modified.

The runtime's server-provided asset URL includes a `winarm64` directory even though
the requested/returned application platform is `win64` and its processor family is
`64-bit`. The downloader uses Adobe's returned URL rather than rewriting path strings.
Package execution/architecture verification is intentionally outside this milestone.

The computed file hash differs from the manifest's `packageHashKey`, confirming that
the opaque field must not be assumed to be a file SHA-256. A matching byte count and
locally computed hash are a transfer receipt, not Adobe signature verification.

## Remaining limits

- Modern `hdPackage` application manifests only; legacy MSI/RIBS entries are discoverable.
- Single, explicitly selected full package; no dependency recursion, delta selection,
  condition evaluation, queue persistence, automatic retries, or resume.
- No installer, desktop interface, ARM hardware validation, or clean-VM install tests.
- Live Adobe catalogs change; rerun catalog discovery instead of assuming sample
  versions remain available.

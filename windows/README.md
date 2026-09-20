# Adobe Downloader for Windows — phase one

Working catalog/manifest CLI and single-package downloader. This does **not** install
applications yet or produce a complete dependency-resolved installation set.
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
Network/validation errors return exit code 1. There is no resume/retry queue yet.

SHA-256 is computed locally. Supply `--sha256 <64 hex characters>` to compare against
an independently trusted digest. Adobe's `packageHashKey` is retained as
`OpaqueHashKey`; it is **not** treated as a SHA-256 checksum. Adobe validation/signature
handling belongs to phase two. Downloaded packages are not claimed to be authenticated
beyond HTTPS unless an expected digest is supplied and matches.

Manifest output retains dependencies, processor family, and condition expressions.
Only explicitly selected packages are downloaded; conditions and dependencies are
not automatically evaluated. Raw Adobe manifest JSON can be saved with
`manifest ... --out <new-file.json>` for further inspection.

## Project map and roadmap

See `docs/WINDOWS-PORT-PLAN.md` and `docs/PHASE-1-VALIDATION.md`.
Open `graphify-out/graph.html` for the local interactive map. Graph outputs and
downloaded payloads are ignored by Git.

```powershell
pwsh -File scripts/graphify.ps1 query 'CatalogClient ManifestClient' --budget 1200
pwsh -File scripts/graphify.ps1 update .
pwsh -File scripts/graphify.ps1 export html
```

AST updates are local. Use the Graphify skill for semantic updates to documentation.

# Windows port

Baseline: upstream `36ebd80` (3.1.0). Preserve the macOS application as a behavioral reference.
License: existing GPLv3 applies to the port.

## Preparation (done)

- Installed Graphify 0.9.65 and its Codex skill in the user's isolated tools environment.
- Indexed 170 supported files (155 code, four documents, 11 images); initial graph has
  4,461 nodes, 11,151 edges, and 194 communities.
- `graphify-out/graph.html`, `graph.json`, and `GRAPH_REPORT.md` are local generated artifacts.
  `file-inventory.json` accounts for tracked files including unsupported configuration formats.
- Graph health is explicitly recorded: 18 native C/header files partially parsed;
  unresolved endpoints and merged edge contexts mean this is a navigation aid, not proof.
  Host semantic-agent token counts are unavailable. Benchmark ratios compare against
  reading the whole corpus and are not measured session savings.

## Architecture

C# core library plus a CLI under `windows/`, followed by a WPF desktop shell.
Use the installed .NET 8 SDK for the initial proof; upgrade to a supported release SDK
before shipping (phase five). The core owns catalog/metadata/download operations and
has no UI dependency. Installation will be a separate Windows-specific component.

Reference entry points:

| Concern | Upstream reference | Windows treatment |
| --- | --- | --- |
| API requests | `Services/NewNetworkService.swift`, `Commons/NewStructs.swift` | HttpClient, FFC v6 XML and application v3 JSON |
| Catalog models | `Utils/NewJSONParser.swift` | Windows-only records and XML parser |
| Package metadata | `Utils/ApplicationJSONParser.swift` | Typed package/dependency records; retain raw manifest |
| Downloads | `Utils/PDM/`, `Utils/NewDownloadUtils.swift` | Streaming downloads first, persistent resume in phase two |
| Installation | `Utils/HDPIM/`, `Utils/InstallManager.swift` | Investigate Windows manifests and implement Windows operations |
| Privilege boundary | `HelperManager/`, `AdobeDownloaderHelperTool/` | Explicit Windows elevation for installation only |
| UI | `Views/`, `ContentView.swift` | WPF shell in phase four |

## Phase 1 — Windows download foundation

Status: **complete**. Evidence and known limits: `PHASE-1-VALIDATION.md`.

Deliver a buildable core and CLI that fetch the win64 catalog, select an exact product
version and locale, retrieve its application manifest, list package URLs, and download
an explicitly selected package with size validation and optional supplied SHA-256.
Preserve dependency metadata without claiming to resolve an installable product yet.
Never execute downloaded payloads in this phase.

Acceptance: offline parser/transport tests, clean build, live catalog and manifest
retrieval, and one complete small official package download with recorded size/hash.
Raw manifest hash fields are opaque until their semantics are verified; don't label
them SHA-256 just because they are hexadecimal.

## Phase 2 — Complete download sets

Resolve transitive dependencies across catalog channels, compatible versions and
architectures, package conditions, language resources, optional modules, and full versus
delta payloads. Persist queues and implement resume with Range/ETag validation, retries,
disk-space checks, and Adobe validation metadata. ARM64 is explicitly tested here.

Acceptance: a complete reproducible download plan for representative products,
dependency-cycle/missing-dependency handling, verified interruption/resume, and no silent
omission of mandatory packages.

## Phase 3 — Windows installation

Inspect real Windows package manifests and establish supported install operations.
Implement extraction, preflight compatibility, a narrow elevated helper, registry and
application registration, required runtime dependencies, logs, and rollback/uninstall.
Preserve Adobe licensing/sign-in requirements. Installation independence from the
Creative Cloud desktop app must be demonstrated per supported product, not assumed.

Acceptance: install, launch, rollback, and uninstall in a disposable Windows VM;
fail closed on unsupported manifest operations. No experiment on existing user installs.

## Phase 4 — Native desktop interface

Build a WPF product catalog, version/language selector, download queue, install progress,
settings, and actionable errors over the tested core. Add accessible keyboard navigation
and a clear distinction between downloaded packages and installed products.

Acceptance: end-to-end UI flow with cancellation/recovery, accessible controls, and
no elevation during catalog browsing or downloads.

## Phase 5 — Release validation and packaging

Upgrade the release toolchain as needed; test supported Windows versions, architectures,
fresh and existing Adobe environments, non-ASCII paths, limited permissions, proxy/offline
failures, and long paths. Produce versioned release packages, license/source notices,
documentation, update strategy, and signing when a signing identity is available.

Acceptance: reproducible release build and documented product compatibility matrix,
including remaining limitations. Ship only installation paths verified in clean VMs.

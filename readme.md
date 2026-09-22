# Flash Creative

A Windows port of [X1a0He's Adobe Downloader](https://github.com/X1a0He/Adobe-Downloader),
working toward a lightweight way to download and install official Adobe application
packages without the Creative Cloud desktop installer.

**Early development: downloads work; Windows installation is not implemented yet.**
**Intel/AMD Windows PCs are the default target (`win64`).** ARM64 is an additional
metadata-planning target; it is not required to run the x64 CLI.
Adobe sign-in, subscriptions, and application licensing requirements still apply.
Flash Creative is an independent project and is not affiliated with Adobe.

## Credit and provenance

The original **Adobe Downloader** was created by **X1a0He** for macOS. Flash Creative
builds on that project's architecture, Adobe metadata handling, and installation
research. This repository preserves its source, attribution, and Git history, starting
from upstream commit `36ebd80` (release 3.1.0).

- Original project: **https://github.com/X1a0He/Adobe-Downloader**
- Original author: [X1a0He](https://github.com/X1a0He)
- Original documentation: [English](readme-en.md) · [中文](readme-upstream-zh.md)
- License: [GNU GPL version 3](LICENSE). Existing upstream and bundled third-party
  copyright/license notices are retained.

## Current Windows features

- Query Adobe's official Windows product catalog.
- Inspect exact versions, language sets, dependencies, and package manifests.
- Download an explicitly selected package from Adobe HTTPS servers.
- Verify archive segments against Adobe HTTPS SHA-256 metadata, plus optional supplied digests.
- Keep incomplete files separate from completed downloads.
- Resolve supported dependencies into a saved, reviewable full-package download plan.
- Persist a download queue and resume interrupted transfers with validated ETags.
- Audit a completed download set online or offline without changing files.
- Select optional modules/features and resolve dependency constraints with bounded backtracking.
- Check extracted Windows executable signatures and exact publisher identity.
- Inspect delta update instructions and verify a local delta archive without applying it.
- Plan individual or enterprise deployments, including Lightroom Classic's conditional packages.

Phase two is in progress. Unknown package conditions or module selections stop planning
with an explicit error. Delta application, detached Adobe signature verification, and
ARM hardware validation remain pending; plans use full packages.

The Windows implementation is C#/.NET, under [`windows/`](windows/).
The original Swift/macOS application remains in the repository as the porting reference.

## Build and try it

Requires a .NET 8 SDK/runtime or a compatible development environment.

```powershell
dotnet build windows/AdobeDownloader.Windows.sln -c Release
dotnet test windows/AdobeDownloader.Windows.sln -c Release
dotnet run --project windows/src/AdobeDownloader.Cli -c Release -- --help
dotnet run --project windows/src/AdobeDownloader.Cli -c Release -- catalog --product KBRG
```

See the [Windows usage guide](windows/README.md) and [phase-two validation evidence](docs/PHASE-2-VALIDATION.md).

## Roadmap

1. Windows catalog, metadata, and single-package download foundation — complete.
2. Dependency resolution, complete download plans, resumable transfers, and persistent queues — in progress.
3. Windows installation, elevation, application registration, and rollback.
4. Native desktop interface.
5. Clean-machine validation, release packaging, and documentation.

Details: [Windows port plan](docs/WINDOWS-PORT-PLAN.md).

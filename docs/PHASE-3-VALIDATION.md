# Phase-three installation research

Status: **started, not an installer**. The new `inspect-install` CLI command reads
verified full-package PIMX metadata and inventories requested installation operations.
It does not authorize those operations for execution or modify an Adobe installation.

## Live evidence, 2026-09-24

Both existing local archives were revalidated against fresh Adobe HTTPS segment data.
The current application manifests were fetched again and compared with saved plan
package URL/size. No additional payload download or Adobe executable launch occurred.

| Package | Verified segments | PIMX operations |
| --- | ---: | --- |
| Bridge 16.0.6.9, AdobeBridge16.0-mul-x64 | 325 | 5 assets, 74 registry entries, 1 folder icon, 1 shortcut, 2 permission changes |
| VC14win64 2.0.0.2, VCRedist14-64 | 9 | 1 asset, 1 RunProgram request |

Bridge's PIMX uses property-prefixed LZMA2. Its decoded metadata SHA-256 is
`6a4ac0b9988c0e8e7238a93aca4cc43623b0e69a33de48f09c6f14f21cc324ee`.
The runtime PIMX is plain XML; decoded metadata SHA-256 is
`eff4baddfd1258e3b997ad7a9fa0231662ed00f1936e9166bb172d5a8445b073`.
Local ignored reports: `.local/bridge-install-inspection.json` and
`.local/runtime-install-inspection.json`. Archive digests are recorded in phase-two evidence.

Bridge references INSTALLDIR, AdobeCommon, AdobeCode, StagingFolder, StartMenuSubFolder,
and installLanguage. The runtime uses InstallDir and OSProcessorFamily and requests
`VC_redist.x64.exe /q /norestart` with `isThirdParty=true`. These are reported as data;
Flash Creative does not invoke that command. The asset's `ignoreAsset=true` flag is
preserved, so a future planner can handle it deliberately.

## Validation and boundaries

The full local suite passes **175 tests**; Release build has zero warnings/errors.
New tests cover plain and compressed metadata, XML identity binding, namespace and
DTD rejection, duplicate fields/sections/archive names, depth limits, encoded and
decoded size limits, malformed LZMA2, tampered archives, UTF-16 variables, nested values,
and visible unknown operation kinds. Original archive bytes stay unchanged.

PIMX is capped at 8 MiB encoded and decoded, 64 XML levels, and 100,000 elements;
LZMA2 dictionaries are capped at 64 MiB. The verified archive stays open without
write/delete sharing while its metadata is inspected. Reports are created without overwrite.

Asset, Registry, and RunProgram are recognized inventory categories, not supported
execution instructions. Shortcut, FolderIcon, Permission, and unknown sections remain
visible in UnknownElements. Every report returns CanInstall=false. Adobe detached
PackageValidation signatures remain unverified, separately from HTTPS segment checks.

## Next implementation work

1. Convert a bounded subset of these instructions into a typed Windows install plan,
   with explicit variable resolution, locale/condition handling, target-path checks,
   registry views/types, and rejection of all unsupported fields or commands.
2. Implement file/registry inventory and a durable transaction journal for rollback,
   preserving pre-existing values and user-modified files.
3. Establish the narrow elevation boundary and dependency-execution contract, including
   exact publisher verification and exit/reboot handling for allowed runtime installers.
4. Validate install, launch, rollback, and uninstall in a disposable Windows VM. Native
   x64/ARM64 core CI does not establish Adobe installation compatibility.

No experiments may use the user's existing Adobe installation as a test target.

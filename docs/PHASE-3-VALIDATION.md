# Phase-three installation research

Status: **in progress, not an installer**. The new `inspect-install` CLI command reads
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
Research is now deferred under `VERIFICATION-POLICY.md`, not a blocker for installation work.

## Typed planning and file-content recovery, 2026-09-25

`plan-install` refreshes verified inspection and compiles strict asset/registry operations.
Live Bridge preview: **4 asset mappings, 69 unique machine registry values, 9 blockers**.
The nine blockers are one overlapping asset tree, two registry entries with unsupported
preference/deletion flags, two per-user registry entries, a folder icon, a shortcut,
and two permissions. One identical machine registry write coalesces. MIME registry key
names retain legal forward slashes. The runtime preview retains its ignored asset and
blocks RunProgram until a verified execution contract exists. Both CanExecute values
remain false. Preview variables used only `.local/install-preview-variables.json` values;
no target directories or registry entries were created.

Evidence: `.local/bridge-typed-install-plan-v2.json` and
`.local/runtime-typed-install-plan.json`. Planner conditions use the selected target's
metadata and configured variables; actual host compatibility still needs preflight.

`FileTransaction` is a library-only, unprivileged file-content recovery foundation:

- Requires exact source and previous-content SHA-256, existing target parent directories,
  a new separate journal directory, and an explicit storage budget.
- Saves and flushes original/replacement content before publishing a Prepared journal.
  Uses temporary sibling files and moves for replacement, then records Committed.
- Recovers partial application from Prepared or Committed journals and supports repeated
  rollback. Checks every backup and target before rollback; detected user edits stop it.
- Rejects traversal, duplicate targets, reparse paths, corrupt backups, and overlapping
  journal/target roots. A reserved lock prevents cooperating transactions from overlapping.

This prototype does not restore ACLs, ownership, timestamps, alternate data streams, or
registry state. It is not resistant to a hostile process racing directory changes and
must not be exposed through an elevated helper. It assumes caller-owned directories and
trusted local journals; checks and cooperative locks do not exclude arbitrary writers.
Failed preparation may retain its new journal folder for diagnosis but changes no target
content. Process-interruption recovery is tested; power-loss durability is not established.
All mutation tests use newly created temporary fixtures, never Adobe installations.

Local suite: **206 passing tests**; Release build has zero warnings/errors. New coverage
includes paths/variables, localized values, registry views/types/conflicts, unknown commands,
partial recovery, corrupt backups, user edits, idempotent rollback, budgets, cancellation,
and transaction locks.

## Concrete asset expansion and registry recovery, 2026-09-25

`plan-install` now holds the archive open without write/delete sharing for both fresh
verification and ZIP entry expansion. The supported layout is one `1/` payload tree
plus the matching root PIMX. The planner retains overlapping asset mappings until
file-level expansion can distinguish harmless directory overlap from actual collisions.

Live Bridge 16.0.6.9 preview now resolves **5 assets, 2,549 files, 388 directories, and
69 registry values**, with **8 remaining blockers**. Every payload file is covered and
no concrete destination collisions were found. The remaining blockers are the four
per-user/preference registry operations, one folder icon, one shortcut, and two
permissions. Empty directories are retained. The runtime's ignored asset is accounted
for without install-file writes; its RunProgram remains blocked.

Evidence: `.local/bridge-expanded-install-plan.json` and
`.local/runtime-expanded-install-plan.json`. Both retain CanExecute=false. Entry sizes
in Files are encoded ZIP-entry bytes (potentially LZMA2), not decoded disk-space estimates.
Expansion reads names/metadata only and does not extract or execute payloads. Inputs are
limited to 128 asset mappings and 100,000 ZIP entries; expansion rejects traversal,
case collisions, links, missing/unmapped files, unexpected archive roots, and conflicting
file/directory targets. Direct-file asset semantics remain unsupported.

`RegistryTransaction` adds a library-only recovery prototype within an explicit,
caller-owned Software subtree and 32/64-bit view. It journals Prepared before creating
keys/writing values, then records Committed. Recovery accepts either state, checks all
current values for conflicts first, restores the original types and data, and removes
only empty keys recorded as created. New user values/subkeys prevent key removal.
It never recursively deletes registry keys. Default values and String, ExpandString,
Binary, None, MultiString, DWORD, and QWORD snapshots are covered; ExpandString is read
without environment expansion. Repeated rollback is supported.

All live mutation tests use unique `HKCU\Software\FlashCreativeTests\<GUID>` subtrees
and temporary journal directories, removed after each test. No machine-wide or Adobe
registry keys were modified. Tests reproduce partially applied Prepared journals,
user edits, wrong baselines, duplicate targets, cancellation, wrong scope, and retention
of user-added values. Total local suite: **235 passing tests**, zero build warnings/errors.

Registry journals are trusted local state and have a 16 MiB bound. Cooperative locking
is shared only by transactions using the same journal parent and exact scope; overlapping
subtree scopes or arbitrary writers are not excluded. Registry symbolic links, journal
ACL/authentication, hostile races, registry security descriptors, combined file/registry
transactions, and elevation are not handled by this prototype. Do not expose it through
an elevated helper yet. Directory contents/registry snapshots can contain private data;
production journal storage must have access controls and a retention policy.

## Next implementation work

1. Implement user-context semantics, shortcuts, icons, and supported permissions;
   turn the concrete file map into verified extraction and installation operations.
2. Preserve file metadata and registry security descriptors, combine file/registry recovery,
   and harden handles, journal trust, and concurrency before adding elevation.
3. Establish the narrow elevation boundary and dependency-execution contract, including
   exact publisher verification and exit/reboot handling for allowed runtime installers.
4. Validate install, launch, rollback, and uninstall in a disposable Windows VM. Native
   x64/ARM64 core CI does not establish Adobe installation compatibility.

Microsoft references: [HKCR and explicit machine/user Classes stores](https://learn.microsoft.com/en-us/windows/win32/sysinfo/hkey-classes-root-key),
[alternate registry views](https://learn.microsoft.com/en-us/windows/win32/winprog64/accessing-an-alternate-registry-view).

No experiments may use the user's existing Adobe installation as a test target.

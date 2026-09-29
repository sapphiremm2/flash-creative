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


## Shell and initiating-user preview follow-up

The planner now emits typed Shortcut and FolderIcon records. Shortcut names require
an exact locale and safe filename; targets and destinations require explicit absolute
root variables. Unknown fields, duplicate fields/locales/destinations, traversal,
device names, and alternate data streams are rejected. Folder icons currently accept
ICO paths only. Both operations retain explicit recovery blockers: no shell artifacts
are created. The subsequent asset-expansion pass checks their targets against the
verified file map.

Plain HKCU values now carry the current process user's SID, captured through
WindowsIdentity rather than installation variables. A UserContext blocker remains
until execution validates that identity and the loaded user hive. This is a preview
captured in the initiating process, not an authorization token for an elevated helper.
Registry preference/recursive-delete attributes and Permission instructions still
block. No original eight Bridge execution blockers are claimed resolved by this work.

Validation: Release build has zero warnings/errors; all 246 local tests pass.
Eleven additional cases cover shell parsing, unsafe names, locale selection,
duplicate/unknown fields, initiating-user identity, and retained preference blockers.
No Adobe applications, shell artifacts, or production registry keys were changed.


## Shell targets and generated-file collisions

Asset expansion now requires shortcut targets and icon files to exist in the mapped
payload, and folder-icon destinations to be mapped directories. Ignored payloads do
not satisfy these checks. Generated shortcuts and desktop.ini destinations are checked
case-insensitively against payload files, required directories, each other, and parent
paths. An existing desktop.ini in the payload blocks rather than being overwritten.
The shell operation count is bounded at 10,000. These checks do not inspect existing
installed files or authorize execution; shell recovery blockers remain in place.

Validation: 253 local tests pass, including seven new target/collision cases. No shell
artifacts were created. Shortcut creation, desktop.ini merging, file attributes and
ACL recovery, registry preferences/permissions, and elevation remain implementation work.


## Folder icon transaction prototype

FolderIconTransaction creates a UTF-16 desktop.ini with a relative ICO path, applies
Hidden/System file attributes and the folder ReadOnly flag, and journals the original
folder attributes before mutation. Recovery verifies content and folder attributes
before removing its INI and restoring the folder flags. Prepared journals and repeated
recovery are supported. Existing desktop.ini files are refused without modification;
merging arbitrary existing settings is not implemented. The ICO must already exist
inside the owned folder; this helper does not validate icon content or package trust.

This is an unprivileged prototype for caller-owned directories and trusted local journals,
not a CLI or elevated execution boundary. It uses the same exact-folder cooperative lock
as FileTransaction. It does not protect against hostile writers, authenticate journals,
restore directory timestamps/ACLs, or coordinate the full installation transaction.
Shell execution blockers remain until integration and recovery hardening are complete.

Seven isolated temporary-folder tests cover creation, Unicode/attributes, rollback,
existing settings, edited content/attributes, partial Prepared state, invalid scope,
and cancellation. All 260 local tests pass; no existing Adobe folders were changed.
Implementation follows Microsoft's desktop.ini folder customization guidance:
https://learn.microsoft.com/en-us/windows/win32/shell/how-to-customize-folders-with-desktop-ini


## Integrated staging and unprivileged recovery (2026-09-27)

Full-package `stage-install` now refreshes the Adobe manifest and segment hashes while
holding the archive against writes, validates the asset map, and decodes ZIP/ZIP-LZMA2
payloads into a new staging directory. It limits decoded bytes and decoder dictionaries,
records per-file SHA-256 and decoded sizes, and publishes the directory only on success.
Receipts remain inventory, not elevated authorization. Unsupported command blockers remain.
A live Bridge 16.0.6.9 run staged 2,549 files / 2,792,681,610 bytes under `.local/`.
No installation paths were written. The eight remaining blockers now concern execution
integration rather than silently unrecognized Bridge commands.

WindowsShortcut creates real links on an STA thread through IShellLinkW/IPersistFile,
reloads its own generated link without Resolve, verifies target/working directory/empty
arguments, and returns a staged hash. FileTransaction can publish and roll back new links.
Tests include spaces and Unicode. No target is launched.

Registry preferences use an explicit port policy, not a claim about undocumented Adobe
flags: initialize absent values only, preserve existing values, and retain preferences
on uninstall. Recursive-delete requests are retained for audit but never enacted.
RegistryPlanCompiler binds HKCU to the initiating process SID and an explicit owned scope.
RegistryTransaction now distinguishes failed-install rollback from uninstall and journals
Uninstalling before mutations, preserving preferences even after an interrupted uninstall.

RegistryPermissionTransaction supports an additive, non-inheriting Everyone/ReadKey ACE
on an existing owned key. It snapshots/restores DACL rules and protection, preserving
conflicting edits. Windows may change the auto-inherited bookkeeping bit; equivalence
ignores only that bit, not ACEs, masks, identities, ordering, or protection. Permission
manifest planning accepts only this narrow operation; machine execution stays blocked.
All ACL mutation tests use unique HKCU test subtrees.

DirectoryTransaction records newly created directories and removes only recorded empty
directories during recovery. InstallationTransaction coordinates directories, new files,
registry values, permissions, and icons with durable parent/child journals. It reverses
completed/partially started steps, skips already-recovered children, detects missing
completed journals, and binds recovery to the expected work fingerprint. It is restricted
to caller-owned roots, new-file installs, and current-user registry scopes. It cannot
launch executables or perform elevated/machine installation. Its journals remain trusted
local state, and it is not hardened against hostile writers or overlapping sessions.

Validation at this checkpoint: 294 local tests pass. Isolated fixtures exercise complete
apply/rollback/uninstall, late failure, interrupted parent recovery, missing child journals,
and preserving user changes. Windows Sandbox/Hyper-V are not available on this host;
actual Adobe install/launch validation remains outstanding. Phase three is not complete.

References: [Windows shell links](https://learn.microsoft.com/en-us/windows/win32/shell/links),
[registry security](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-key-security-and-access-rights).


## Runtime preparation and recovery hardening (2026-09-28)

`prepare-runtime` performs fresh Adobe manifest/segment verification, validates the
reviewed VC14win64 / 2.0.0.2 / VCRedist14-64 identity and exact PIMX command, extracts
the ignored installer asset as a resource, and verifies Microsoft Corporation's
embedded signature. The publisher and arguments come from a code-owned allowlist.
Unknown versions, additional payloads, and changed arguments fail closed. The command
never executes the runtime. The live 18,558,944-byte executable passed Authenticode:
SHA-256 `8995548DFFFCDE7C49987029C764355612BA6850EE09A7B6F0FDDC85BDC5C280`.
Adobe identifies this package as `zip-deflated`; staging now supports that explicit
format alongside `zip` and `zip-lzma2`. Empty/unknown compression remains unsupported.

LockedWindowsFile holds the file against writes/deletion and ancestor directory handles
against rename. Reparse points are checked on the opened handles. Inspection/staging use
this lease across archive verification/reading. VerifyAndHold returns a signature lease
that remains alive through the caller's scope. prepare-runtime additionally checks that
the executable bytes match the fresh staging digest. CLI output is diagnostic; the lease
is released when the command exits and cannot authorize execution in a later process.

The reviewed runtime policy classifies success, reboot-required, reboot-initiated, and
failure exit codes. No launcher, durable launch/exit journal, or elevated runtime helper
is enabled yet. Runtime policy and verification are prerequisites, not completed execution.

InstallationSessionGate serializes participating coordinators for the current Windows
user/session using a named mutex held by a dedicated owner thread. Cancellation while
waiting does not execute work. Standalone transaction helpers and other Windows sessions
are outside this cooperative gate; it is not a security boundary.

New transaction journals and staging directories use protected ACLs granting access only
to the current user, SYSTEM, and Administrators. Existing folders are not adopted. The
current user can still alter these files: this improves privacy, not authenticity for an
elevated helper. Journal authentication, privileged storage, and hostile-race hardening
remain necessary before machine installation.

Validation: Release build and 315 local tests pass, including held-file/ancestor locks,
failed-verification cleanup, concurrent-session cancellation, private ACL inheritance,
and runtime rejection cases. The previous 294-test integration checkpoint passed native
x64 and ARM64 CI: https://github.com/sapphiremm2/flash-creative/actions/runs/36340251122.
No Adobe app or Microsoft runtime was executed on this host.


Recovery follow-up: parent journals now reject contradictory Prepared/Committed step
counts and require all completed child journals before any reverse mutation. Three
additional cases pass (318 local tests total). The 315-test runtime/storage checkpoint
passed native x64 and ARM64 CI:
https://github.com/sapphiremm2/flash-creative/actions/runs/36466362606.


Publisher binding follow-up: Authenticode identity is now read from the successful
WinVerifyTrust provider's primary signer chain, rather than separately extracting a
certificate from the file. Positive verification of the installed signed .NET host and
the staged Microsoft runtime passes; a wrong expected publisher is rejected. All 319
local tests pass. Provider structures follow Microsoft's CRYPT_PROVIDER_SGNR and
CRYPT_PROVIDER_CERT definitions; native ARM64 CI validates the interop layout as well.


Elevation request-format checkpoint: InstallationRequestCodec now strictly parses a
bounded selection-only request, rejecting additional privileged instructions, duplicate
fields, missing values, unsupported versions, and invalid/duplicate/excessive archive
inputs. It does not authenticate peers or authorize execution. Eight new cases pass
(327 local tests total). The owner confirmed no disposable Windows VM is currently
available; final Adobe installation/launch acceptance remains outstanding, along with
the authenticated privileged helper and actual runtime execution integration.

## Disposable VM and runtime journal checkpoint (2026-09-28)

The owner subsequently authorized creating a Windows VM on the external Toshiba drive.
VirtualBox 7.2.20 is installed, and a Windows 11 Enterprise 25H2 evaluation guest is being
installed under `D:\FlashCreative-VM`, with 8 GiB RAM, four virtual CPUs, and a dynamic
100 GiB disk. Microsoft's complete ISO SHA-256 matched. UEFI, TPM 2.0, and Secure Boot
are configured, with the unattended template's hardware-check bypasses removed.
Guest installation and application acceptance are not yet complete. Reproduction and
storage details are in [Windows VM validation](WINDOWS-VM-VALIDATION.md).

The new guest-only `scripts/vm-smoke.ps1` captures environment evidence, CLI startup,
and fresh runtime preparation without executing an installer. PowerShell parsing and
host rejection were checked locally: a nonmatching host exits before creating output.
A self-contained x64 CLI bundle and reviewed runtime inputs are ready for guest transfer.

RuntimeExecutionJournal adds durable launch intent, process identity, and exit records
to the unprivileged prototype. Incomplete launch states require reconciliation and never
permit automatic retry. Completion preserves ordinary failures, reboot-required, and
reboot-initiated results. Tests cover interrupted states, cancellation, wrong execution
identity/digest, contradictory records, duplicate/unknown fields, and oversized input.
The Release build is clean and all 343 local tests pass. This journal does not launch
processes or authenticate recovery input; privileged integration remains outstanding.

Append-only recovery follow-up: the runtime journal now retains its full four-state
history in `runtime.jsonl`, using write-through and a disk flush instead of replacing
the prior state file. Partial tails, missing record terminators, duplicate states, and
changed process identities fail closed. The tests simulate damaged records; they do not
claim physical power-loss validation. All 347 local tests pass. The earlier 343-test
checkpoint passed native x64 and ARM64 CI:
https://github.com/sapphiremm2/flash-creative/actions/runs/36500051146.
Guest smoke evidence now includes existing VC runtime registry values in both registry
views and hashes of the CLI, core assembly, and smoke script.

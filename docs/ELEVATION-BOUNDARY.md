# Phase-three privileged installation boundary

Status: bounded request codec implemented; authenticated transport and privileged execution
pending. The current transaction helpers are unprivileged
prototypes. This document defines the next boundary; it does not authorize those
helpers to run elevated or claim that a secure helper exists.

## Request and identity

InstallationRequestCodec implements a 64 KiB, depth-limited, versioned selection-only
format. It rejects unknown/duplicate JSON fields, missing fields, duplicate archive
identities, invalid identifiers/locales/platforms, and excessive declared input sizes.
No paths, publishers, user identity claims, operation lists, or caller hashes are accepted.
These checks are parsing only: the helper still must authenticate peers, prevent replay,
and independently verify all metadata and archive contents. No transport or elevation
entry point currently consumes this codec.

The helper accepts a product/version/platform/locale selection and bounded archive
inputs. It must not accept executable paths, publisher names, command lines, arbitrary
registry instructions, or a saved WindowsInstallPlan as privileged authorization.

Bind each request to a single initiating process/user and one transaction identifier.
The initiating user may differ from the account used for UAC credentials. Capture and
validate the initiating identity before elevation. Perform per-user registry operations
in that user's unelevated process, with its own recovery journal; elevated HKCU must
never stand in for that user.

The transport must authenticate both peers and prevent replay/cross-request mixing.
Client-provided user SIDs, process IDs, journal filenames, and local hash receipts do
not establish identity or authority. Archive transfer must not grant the helper an
arbitrary privileged-file read capability. Select and test the transport before adding
an install CLI switch.

## Privileged preparation

The helper reconstructs its own plan from current Adobe metadata, validates every
redirect and package segment, and retains the explicit detached-signature limitation.
It independently checks selected package completeness, host compatibility, requested
modules, dependency ordering, and supported PIMX commands.

Resolve machine destinations from trusted Windows known-folder locations and the
supported product policy. Apply explicit allowlists for registry hives/subtrees and
runtime identities. User-supplied variables and strings from downloaded manifests
cannot expand the helper's authority to arbitrary system destinations.

Use new administrator/SYSTEM-owned staging and journal directories with protected
DACLs. Verify ownership, effective write access, and reparse-point handling before
using existing privileged state. The current PrivateStorage implementation intentionally
grants the initiating user full access and is unsuitable for this role.

Hold validated objects across verification and use. LockedWindowsFile covers read-only
file/ancestor leases, but does not secure mutable journal trees, prevent DLL side-loading,
or make generic path-based mutations safe. Registry handles need their own identity,
view, scope, and link protections. Replacing a file must preserve the supported metadata
or explicitly reject that replacement. The current coordinator only supports new files.

## Execution and recovery

Keep the runtime executable under a held signature lease through process launch and
completion. Publisher and arguments must come from RuntimeInstallerPolicy. The reviewed
VC runtime is not authorization for another runtime version, executable, or argument set.
Use a protected working directory and controlled launch environment. Avoid a command
shell and do not inherit unnecessary handles.

Persist a launch-intent record before starting an external installer, followed by process
identity and completion/exit status. A crash between launch and recording the process is
an uncertain outcome: never automatically rerun the installer or report success. Runtime
installation can affect shared components and cannot be reversed by deleting staged files.
Cancellation after launch must not blindly kill an installer or trigger concurrent recovery.

RuntimeExecutionJournal now implements this bookkeeping for trusted, unprivileged fixtures:
Prepared -> LaunchIntent -> Started -> Completed. Version-two records append to a bounded
JSON-lines history with write-through and an explicit disk flush before returning.
Incomplete tails are rejected rather than falling back to an earlier Prepared record;
the complete sequence and process identity continuity are checked during recovery.
Inspection binds the execution ID and executable digest, rejects contradictory or malformed
records, and treats both incomplete launch states as requiring reconciliation. It never
retries or kills a process, and a stored PID is not evidence of a currently running process.
The launcher must await each journal write before its next side effect and keep tracking
an already launched process independently of UI cancellation. Protected storage, peer
authentication, actual process execution, and reliable reconciliation remain unimplemented.
The current user's ability to edit this prototype journal prevents its privileged use.

Record reboot-required and reboot-initiated outcomes separately from ordinary success.
Do not automatically uninstall shared Microsoft runtimes during application removal.
Ordinary failures and newer-version conflicts need an explicit dependency-resolution
policy rather than being treated as success by default.

Coordinate machine changes and unelevated user changes with recoverable state transitions.
The existing parent/child journal protocol is useful test evidence but does not authenticate
privileged recovery inputs. A session mutex prevents cooperating concurrent operations;
it does not defend against hostile processes or prove that an abandoned transaction is safe.

## Required validation before enabling installation

- Authenticate peers and reject wrong-user, replayed, oversized, and malformed requests.
- Reject user-writable or substituted privileged journals/staging, reparse points, registry
  links, destination changes, and signature/publisher mismatches.
- Exercise UAC cancellation and elevation with a different administrator account; verify
  that user registry writes still target the initiating user.
- Test crashes before and after each durable state transition, including runtime launch,
  and preserve user edits on recovery/uninstall.
- In a disposable Windows VM, install the complete supported Bridge package set, launch
  the actual application, observe Adobe licensing/sign-in, and validate rollback/uninstall.
- Repeat applicable tests on native x64 and ARM64. Core CI alone does not establish Adobe
  application compatibility. Do not test these mutations against an existing user install.

Existing evidence and limitations: [phase-three validation](PHASE-3-VALIDATION.md) and
[verification policy](VERIFICATION-POLICY.md).

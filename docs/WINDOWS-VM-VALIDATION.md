# Disposable Windows validation VM

The project owner authorized installing a local VM on 2026-09-28. The host is
Windows 11 Home x64 with 32 GiB RAM. VirtualBox 7.2.20 was installed through
WinGet's Oracle.VirtualBox package, with installer hash verification.

## Storage and guest configuration

The local VM is named `FlashCreative-Win11-Test`. It was initially stored entirely
under `D:\FlashCreative-VM` on the external Toshiba NTFS drive, which had about
413 GiB free. On October 1 the owner approved using the internal SSD after setup
showed roughly 30 ms external-drive latency. Both the base VDI and active differencing
VDI now reside under `C:\FlashCreative-VM\FlashCreative-Win11-Test` (about 18.3 GiB
combined at transfer). The configuration, saved memory, diagnostic snapshot metadata,
logs, installation media, credentials, and test inputs remain under the original D:
root. Keep both drives available. VirtualBox itself is installed under Program Files.

The full `movevm` operation crashed in VBoxSVC after transferring the active disk.
The base disk was subsequently transferred through `modifymedium --move`. On October 2
VirtualBox reported both media as created, retained the parent/child UUID chain, and
referenced their SSD locations. The VM remained saved; no snapshot was discarded.
The configured snapshot folder still points to D:; VirtualBox refuses to change it while
snapshots exist. After taking a powered-off snapshot, move its new writable VDI to the
SSD through `modifymedium --move` before booting. The local continuation worker now
does this, preserving the existing snapshots. Do not assume that the entire VM
directory moved to C:.

The guest was created with four virtual CPUs, 8 GiB RAM, and a dynamically allocated 100 GiB
VDI. This is a disk capacity limit, not an up-front 100 GiB allocation. Snapshots
and installation media consume additional host space. Keep the external drive
connected while the guest is running; shut down the guest before unplugging it.

UEFI, TPM 2.0, and Secure Boot are configured. Networking is NAT; clipboard and
drag-and-drop integration are disabled. There are no host shared folders or raw
host disks attached. VirtualBox Guest Additions provide guest process/file control.
The VM directory has a protected DACL for the current host user, SYSTEM, and
Administrators. Generated guest credentials and unattended setup files remain
local to that directory and must never be committed or included in test evidence.

## Windows image provenance

Use Microsoft's [Windows 11 Enterprise evaluation download page](https://www.microsoft.com/en-us/evalcenter/download-windows-11-enterprise).
The downloaded image is the English-US x64 Windows 11 Enterprise 25H2 evaluation,
build 26200.6584. Its size is 7,092,807,680 bytes. The complete download matched
Microsoft's published SHA-256:

```text
A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9
```

This is evaluation software, not a permanent Windows license. The local unattended
template derives from VirtualBox's installed template, with its Windows hardware
check bypass commands removed. Guest hardware and Secure Boot must be checked
again from the installed OS.

## Preparation smoke test

The initial VM is a preparation/recovery test environment, not yet a qualified Bridge
graphics environment. [Bridge 16 requirements](https://helpx.adobe.com/bridge/desktop/get-started/technical-requirements.html)
include AVX2, 8 GB RAM, DirectX 11, 2 GB GPU memory, and a 1280 x 800 display.
The initial VM configuration has 3D acceleration disabled and 128 MB virtual video RAM.
Before interpreting application-launch results, inspect guest CPU and graphics support,
enable and verify suitable graphics where available, and record any unmet requirements.
VirtualBox advertises AVX2 in this VM's CPU log, but the installed guest must still be
checked. Installer success alone does not establish graphics compatibility.

Publish the current CLI with `dotnet publish`, Release, `win-x64`, self-contained.
Copy the published directory to `cli` under a guest test-payload directory. Place
the reviewed runtime download plan at `inputs\runtime-plan.json` and the original
Adobe runtime archive at `inputs\VCRedist14-64.zip`. Copy files through Guest
Control, avoiding a writable host share.

Take a clean, powered-off snapshot after Windows and Guest Additions are ready.
Record the exact source commit and CLI hash used for each run. Inside the selected
guest, run `scripts/vm-smoke.ps1` with its expected hardware UUID, payload directory,
and a new output directory. The script rejects a non-VirtualBox host or a different
guest UUID before creating output. Preparation can run without elevation. Secure Boot
and TPM diagnostics record `Available`, `Value`, and `Error`; missing read permissions
are reported as unavailable, never as disabled or successfully validated hardware.
The environment record also reports whether the guest process is elevated. Obtain
separate administrator diagnostics when those hardware properties must be validated.

The script captures OS/hardware evidence, existing VC runtime registry values in both
views, hashes of the CLI/core/script, CLI startup, and `prepare-runtime` results.
It also captures `host-info` from the self-contained CLI, including the guest OS/process
architectures and AVX2 availability as seen by .NET.
Preparation re-fetches Adobe verification metadata and checks the Windows signature
and expected Microsoft publisher. A successful preparation is **not** installer
execution or Adobe application validation. Copy results back to a local evidence
directory; do not copy credentials or unattended setup media into reports.

## Bounded setup readiness and recovery

Use `pwsh -File scripts/wait-vm-ready.ps1` (PowerShell 7.3+) on the host with the explicitly selected VM UUID,
guest username, and local password-file path. It requires a running VM, Guest Additions,
`SystemSetupInProgress = 0`, and the completion marker in `C:\vboxpostinstall.log`.
A Guest Additions version property alone is insufficient. This check does not establish
installer or application compatibility.

Set `-TimeoutMinutes 15 -SaveStateOnTimeout` for unattended waits. On timeout the script
saves and suspends the selected VM, retaining guest memory and disk state rather than
leaving it consuming CPU indefinitely. A saved state consumes additional drive space
and is not a clean baseline snapshot. A failed save is reported explicitly. Resume the
same VM deliberately before retrying; the script never starts or resets it automatically.

On October 1 the first login was still at the Welcome screen, with no Guest Additions
readiness and no disk-file modification since September 29. The earlier local worker had
timed out but left the guest running. Its readiness function now uses the bounded script.
The diagnostic `Setup-before-paravirt-test` snapshot is retained; it is not an installed
Windows baseline. Guest paravirtualization had been changed to `legacy` (effective `none`).
After an unanswered ACPI shutdown request, the disposable guest was powered off and
restarted with one virtual CPU as a diagnostic, following a similar
[VirtualBox issue report](https://github.com/VirtualBox/virtualbox/issues/653).
That report does not establish the cause of this VM's stall. The one-CPU configuration
is not a qualified Windows/Adobe acceptance environment. Host Hyper-V and security
settings were not changed. Restore and validate a suitable CPU configuration before
claiming compatibility results.

On October 2 the resumed guest showed the post-installation completion marker and
successful Guest Additions installation. Its service had not yet started. Windows
shut down normally; the next boot with four virtual CPUs passed the complete readiness
probe and accepted guest commands. The command token is unelevated. A powered-off
`Clean-Windows-25H2` snapshot was then taken, UUID
`7d1c8562-9314-4cec-b5ed-37dd691614b9`, before test payload transfer. Its new writable
disk was moved to the SSD before reboot. This establishes the test baseline, not
Adobe installation or launch acceptance.

Boot reliability remains unresolved: the next four-CPU legacy-provider boot stalled
before Guest Additions, and a clean restore using the default provider stalled in
`CpuMpPei.efi` before a display was available. The baseline was restored again and
one CPU with the legacy provider selected for the preparation smoke trial. Treat
results from this diagnostic configuration separately from Windows/Adobe hardware
compatibility; a single successful four-CPU boot did not establish repeatability.

The one-CPU preparation smoke passed on October 2; its results and executable digest
are recorded in [phase-three validation](PHASE-3-VALIDATION.md#first-clean-guest-preparation-pass-2026-10-0203).
Separate administrator diagnostics on October 3 confirmed guest Secure Boot and TPM
readiness. The smoke itself remained unelevated and did not execute an installer.
For guest file transfers, use explicit destination filenames for individual files;
the directory transfer used here placed the payload contents directly under
`C:\FlashCreativeSmoke`, without an extra `TestPayload` directory.

## Remaining acceptance gates

The VM does not make the existing unprivileged transaction prototypes safe to run
elevated. Implement and validate the [privileged boundary](ELEVATION-BOUNDARY.md)
before testing the actual installation entry point. Keep separate evidence for:

- CLI and preparation smoke checks on a clean guest.
- Authenticated helper transport and privilege/storage rejection cases.
- Runtime launch, exit, reboot, cancellation, and interrupted recovery behavior.
- Full supported Bridge package installation, application launch and Adobe sign-in.
- Rollback and uninstall, preserving user edits and shared runtimes.

Restore the appropriate clean snapshot between destructive scenarios. Native ARM64
CI remains useful core coverage; this x64 VM does not establish ARM64 Adobe app
compatibility. See [phase-three evidence](PHASE-3-VALIDATION.md) for completed checks.

# Disposable Windows validation VM

The project owner authorized installing a local VM on 2026-09-28. The host is
Windows 11 Home x64 with 32 GiB RAM. VirtualBox 7.2.20 was installed through
WinGet's Oracle.VirtualBox package, with installer hash verification.

## Storage and guest configuration

The local VM is named `FlashCreative-Win11-Test`. Its configuration, virtual disk,
snapshots, logs, and Windows installation media live under `D:\FlashCreative-VM`
on the external Toshiba NTFS drive. The host initially had about 413 GiB free on
that drive. VirtualBox itself is installed under the host's Program Files.

The guest uses four virtual CPUs, 8 GiB RAM, and a dynamically allocated 100 GiB
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
guest UUID before creating output. Run with guest administrator rights so Windows
can report Secure Boot and TPM state.

The script captures OS/hardware evidence, existing VC runtime registry values in both
views, hashes of the CLI/core/script, CLI startup, and `prepare-runtime` results.
Preparation re-fetches Adobe verification metadata and checks the Windows signature
and expected Microsoft publisher. A successful preparation is **not** installer
execution or Adobe application validation. Copy results back to a local evidence
directory; do not copy credentials or unattended setup media into reports.

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

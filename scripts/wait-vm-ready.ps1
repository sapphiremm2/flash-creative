#requires -Version 7.3
# Host-side readiness check for the explicitly selected disposable VirtualBox VM.
# Does not start, reset, power off, or install anything in the guest.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Guid]$VmId,
    [Parameter(Mandatory = $true)][string]$GuestUser,
    [Parameter(Mandatory = $true)][string]$PasswordFile,
    [ValidateRange(1, 120)][int]$TimeoutMinutes = 15,
    [string]$VBoxManage = "$env:ProgramFiles\Oracle\VirtualBox\VBoxManage.exe",
    [switch]$SaveStateOnTimeout
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false # Probe exit codes are handled below.
Set-StrictMode -Version Latest
if ($VmId -eq [Guid]::Empty) { throw 'An explicit nonempty VM UUID is required.' }
$passwordPath = (Resolve-Path -LiteralPath $PasswordFile).Path
if (-not (Test-Path -LiteralPath $passwordPath -PathType Leaf)) { throw 'Password file is missing.' }
if (-not (Test-Path -LiteralPath $VBoxManage -PathType Leaf)) { throw 'VBoxManage is missing.' }
$auth = @("--username=$GuestUser", "--passwordfile=$passwordPath")
$probe = 'if ((Get-ItemProperty HKLM:\SYSTEM\Setup).SystemSetupInProgress -eq 0 -and (Test-Path C:\vboxpostinstall.log) -and (Select-String -LiteralPath C:\vboxpostinstall.log -SimpleMatch "*** done" -Quiet)) { exit 0 } else { exit 2 }'
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
while ([DateTime]::UtcNow -lt $deadline) {
    $info = & $VBoxManage showvminfo $VmId --machinereadable 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the selected VM.' }
    if (($info -join "`n") -notmatch '(?m)^VMState="running"$') {
        throw 'The selected VM is not running; readiness was not established.'
    }
    $version = & $VBoxManage guestproperty get $VmId '/VirtualBox/GuestAdd/Version' 2>&1
    if ($LASTEXITCODE -eq 0 -and ($version -join '') -match '^Value: .+') {
        $remainingMs = [Math]::Min(30000, [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds))
        $null = & $VBoxManage guestcontrol $VmId run @auth `
            --exe='C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
            "--timeout=$remainingMs" --wait-stdout --wait-stderr -- `
            -NoProfile -NonInteractive -Command $probe 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Output "Guest $VmId completed Windows setup and VirtualBox post-installation."
            return
        }
    }
    $remainingMs = [Math]::Min(10000, [Math]::Max(0, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds))
    if ($remainingMs -gt 0) { Start-Sleep -Milliseconds $remainingMs }
}
if ($SaveStateOnTimeout) {
    $null = & $VBoxManage controlvm $VmId savestate 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Readiness timed out and saving VM state failed. Inspect the VM; it may still be running.'
    }
    throw 'Readiness timed out. VM state was saved and the guest suspended; no validation passed.'
}
throw 'Readiness timed out. The VM was left running; no validation passed.'

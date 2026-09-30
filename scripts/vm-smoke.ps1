# Run inside a disposable VirtualBox guest, after taking a clean snapshot.
# This checks preparation only; it does not launch the staged installer.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Guid]$ExpectedVmId,
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$machine = Get-CimInstance Win32_ComputerSystem
$identity = Get-CimInstance Win32_ComputerSystemProduct
if ($machine.Model -ne 'VirtualBox' -or [Guid]$identity.UUID -ne $ExpectedVmId) {
    throw 'This script must run inside the explicitly selected disposable VirtualBox guest.'
}
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory for each smoke run.' }
$cli = Join-Path $payload 'cli\AdobeDownloader.Cli.exe'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'Published CLI is missing.' }
$null = New-Item -ItemType Directory -Path $output

$os = Get-CimInstance Win32_OperatingSystem
$runtimeBaseline = foreach ($view in @('Registry64', 'Registry32')) {
    $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::$view)
    try {
        foreach ($architecture in @('x64', 'x86', 'arm64')) {
            $key = $hive.OpenSubKey("SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\$architecture")
            try {
                [ordered]@{
                    View = $view
                    Architecture = $architecture
                    Present = $null -ne $key
                    Version = if ($null -ne $key) { $key.GetValue('Version') } else { $null }
                    Installed = if ($null -ne $key) { $key.GetValue('Installed') } else { $null }
                }
            } finally { if ($null -ne $key) { $key.Dispose() } }
        }
    } finally { $hive.Dispose() }
}
$environment = [ordered]@{
    VmId = $identity.UUID
    Model = $machine.Model
    Windows = $os.Caption
    Version = $os.Version
    Architecture = $os.OSArchitecture
    SecureBoot = Confirm-SecureBootUEFI
    TpmPresent = (Get-Tpm).TpmPresent
    CapturedUtc = [DateTime]::UtcNow.ToString('o')
    CliSha256 = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash
    CliAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'cli\AdobeDownloader.Cli.dll') -Algorithm SHA256).Hash
    CoreAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $payload 'cli\AdobeDownloader.Core.dll') -Algorithm SHA256).Hash
    SmokeScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    RuntimeBaseline = @($runtimeBaseline)
    InstallerExecuted = $false
}
$environment | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'environment.json') -Encoding UTF8

& $cli --help > (Join-Path $output 'cli-help.txt')
if ($LASTEXITCODE -ne 0) { throw 'Published CLI could not start in the guest.' }
& $cli host-info > (Join-Path $output 'host-info.json')
if ($LASTEXITCODE -ne 0) { throw 'Guest CPU and architecture diagnostics failed.' }
& $cli prepare-runtime --plan (Join-Path $payload 'inputs\runtime-plan.json') `
    --archive (Join-Path $payload 'inputs\VCRedist14-64.zip') `
    --destination (Join-Path $output 'prepared-runtime') `
    > (Join-Path $output 'runtime-preparation.json') 2> (Join-Path $output 'runtime-preparation.stderr.txt')
if ($LASTEXITCODE -ne 0) { throw 'Fresh runtime preparation failed; inspect the captured error.' }

[ordered]@{
    Passed = $true
    Scope = 'CLI startup and fresh Adobe runtime preparation with Windows publisher verification'
    InstallerExecuted = $false
    AdobeApplicationValidated = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
Write-Output "Preparation smoke checks passed. Evidence: $output"

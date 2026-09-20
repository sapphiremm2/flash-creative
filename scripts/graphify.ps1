param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GraphArguments)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$interpreterFile = Join-Path $repoRoot 'graphify-out\.graphify_python'
$pythonPath = if (Test-Path -LiteralPath $interpreterFile) {
    (Get-Content -LiteralPath $interpreterFile -Raw).Trim()
} else {
    Join-Path $env:USERPROFILE '.codex\tools\graphify\Scripts\python.exe'
}
if (!(Test-Path -LiteralPath $pythonPath)) {
    throw 'Graphify Python environment missing. Install graphifyy, or rebuild the graph with the Graphify skill.'
}
Push-Location $repoRoot
try {
    & $pythonPath -m graphify @GraphArguments
    if ($LASTEXITCODE -ne 0) { throw "Graphify exited with code $LASTEXITCODE" }
} finally { Pop-Location }

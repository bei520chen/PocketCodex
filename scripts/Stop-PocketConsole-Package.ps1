$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $PSScriptRoot
$runtimeDirectory = Join-Path $packageRoot ".runtime"
$pidFile = Join-Path $runtimeDirectory "pocket-console.pid"

if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-Host "Pocket Console is not running."
    exit 0
}

$processId = (Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
if ($processId -match '^\d+$') {
    $process = Get-Process -Id ([int]$processId) -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $process.Id -Force
        Write-Host "Pocket Console stopped."
    }
}

Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue

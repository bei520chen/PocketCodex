$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $root ".runtime\pocket-console.pid"

if (-not (Test-Path $pidFile)) {
    Write-Host "Pocket Console is not running."
    exit 0
}

$processId = Get-Content $pidFile -ErrorAction SilentlyContinue
if ($processId -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
    Stop-Process -Id $processId -Force
    Write-Host "Pocket Console stopped."
}
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue

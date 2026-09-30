param([int]$Port = 5087)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $root ".runtime"
$env:Security__Password = (Get-Content (Join-Path $runtime "access-password.txt") -Raw).Trim()
$env:ConnectionStrings__PocketConsole = "Data Source=$(Join-Path $root 'src\PocketConsole.Api\Data\pocket-console.db')"
$env:Security__WorkspaceRoots__0 = Split-Path -Parent $root
$dll = Join-Path $root ".tmp\api-build-attachment\PocketConsole.Api.dll"
Set-Location (Join-Path $root "src\PocketConsole.Api")
& dotnet $dll --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE

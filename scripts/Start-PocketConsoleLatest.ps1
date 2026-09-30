param([int]$Port = 5087)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $root ".runtime"
$pathKeys = @([Environment]::GetEnvironmentVariables().Keys | Where-Object { "$_" -ieq "Path" })
if ($pathKeys.Count -gt 1) {
    $pathValue = $env:Path
    foreach ($pathKey in $pathKeys) { Remove-Item -Path "env:$pathKey" -ErrorAction SilentlyContinue }
    $env:Path = $pathValue
}
$env:Security__Password = (Get-Content (Join-Path $runtime "access-password.txt") -Raw).Trim()
$env:ConnectionStrings__PocketConsole = "Data Source=$(Join-Path $root 'src\PocketConsole.Api\Data\pocket-console.db')"
$env:Security__WorkspaceRoots__0 = Split-Path -Parent $root
$dll = Join-Path $root ".tmp\api-build-attachment\PocketConsole.Api.dll"
if (-not (Test-Path $dll)) { throw "Latest build is missing: $dll" }
$stdout = Join-Path $runtime "pocket-console-$Port.out.log"
$stderr = Join-Path $runtime "pocket-console-$Port.err.log"
$process = Start-Process -FilePath "dotnet" -ArgumentList @($dll, "--urls", "http://127.0.0.1:$Port") -WorkingDirectory (Join-Path $root "src\PocketConsole.Api") -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
Set-Content -LiteralPath (Join-Path $runtime "pocket-console-$Port.pid") -Value $process.Id
Start-Sleep -Seconds 3
if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { Get-Content $stderr -ErrorAction SilentlyContinue; throw "PocketConsole failed to start" }
Write-Host "PocketConsole latest started PID $($process.Id) on $Port"

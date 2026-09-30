param(
    [switch]$Build,
    [int]$Port = 5086,
    [string]$Password,
    [string[]]$WorkspaceRoots
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runtimeDirectory = Join-Path $root ".runtime"
$pidFile = Join-Path $runtimeDirectory "pocket-console.pid"
$stdoutFile = Join-Path $runtimeDirectory "pocket-console.out.log"
$stderrFile = Join-Path $runtimeDirectory "pocket-console.err.log"
$passwordFile = Join-Path $runtimeDirectory "access-password.txt"
New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null

if (-not $Password) {
    if (Test-Path $passwordFile) {
        $Password = (Get-Content -LiteralPath $passwordFile -Raw).Trim()
    }
    else {
        $bytes = New-Object byte[] 24
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $Password = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Set-Content -LiteralPath $passwordFile -Value $Password -NoNewline
    }
}
else {
    Set-Content -LiteralPath $passwordFile -Value $Password -NoNewline
}

if (-not $WorkspaceRoots -or $WorkspaceRoots.Count -eq 0) {
    $WorkspaceRoots = @((Split-Path -Parent $root))
}

if (Test-Path $pidFile) {
    $existingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
    $existingProcess = $null
    if ($existingPid) {
        $existingProcess = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
    }
    $isHealthy = $false
    if ($existingProcess) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/auth/status" -UseBasicParsing -TimeoutSec 2
            $isHealthy = $response.StatusCode -eq 200
        }
        catch { $isHealthy = $false }
    }
    if ($isHealthy) {
        Write-Host "Pocket Console is already running (PID $existingPid)."
        Write-Host "Local URL: http://127.0.0.1:$Port"
        exit 0
    }
    Remove-Item -LiteralPath $pidFile -Force
}

if ($Build) {
    Push-Location (Join-Path $root "src\PocketConsole.Web")
    try { npm install; npm run build } finally { Pop-Location }
    dotnet build (Join-Path $root "PocketConsole.sln")
}

$env:Security__Password = $Password
$databasePath = Join-Path $root "src\PocketConsole.Api\Data\pocket-console.db"
$env:ConnectionStrings__PocketConsole = "Data Source=$databasePath"
for ($index = 0; $index -lt $WorkspaceRoots.Count; $index++) {
    $resolvedRoot = (Resolve-Path -LiteralPath $WorkspaceRoots[$index]).Path
    Set-Item -Path "env:Security__WorkspaceRoots__$index" -Value $resolvedRoot
}

$pathKeys = @([System.Environment]::GetEnvironmentVariables().Keys | Where-Object { "$_" -ieq "Path" })
if ($pathKeys.Count -gt 1) {
    $pathValue = $env:Path
    foreach ($pathKey in $pathKeys) {
        Remove-Item -Path "env:$pathKey" -ErrorAction SilentlyContinue
    }
    $env:Path = $pathValue
}

$runtimeAppDirectory = Join-Path $runtimeDirectory "app"
$runtimeDll = Join-Path $runtimeAppDirectory "PocketConsole.Api.dll"
$debugDll = Join-Path $root "src\PocketConsole.Api\bin\Debug\net9.0\PocketConsole.Api.dll"
$applicationDll = $debugDll
if (Test-Path $runtimeDll) { $applicationDll = $runtimeDll }

$startOptions = @{
    FilePath = "dotnet"
    ArgumentList = @($applicationDll, "--urls", "http://127.0.0.1:$Port")
    WorkingDirectory = (Join-Path $root "src\PocketConsole.Api")
    RedirectStandardOutput = $stdoutFile
    RedirectStandardError = $stderrFile
    WindowStyle = "Hidden"
    PassThru = $true
}
$process = Start-Process @startOptions
Set-Content -LiteralPath $pidFile -Value $process.Id
Start-Sleep -Seconds 2

if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
    Write-Error "Pocket Console failed to start. Check $stderrFile"
}

Write-Host "Pocket Console started (PID $($process.Id))."
Write-Host "Local URL: http://127.0.0.1:$Port"
Write-Host "Access password: $Password"
Write-Host "Password file: $passwordFile"
Write-Host "Tailscale Serve target: http://127.0.0.1:$Port"
Write-Host "After Tailscale is installed and connected, run scripts\Enable-Tailscale.ps1."

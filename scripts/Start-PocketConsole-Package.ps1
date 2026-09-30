param(
    [int]$Port = 5086,
    [string]$Password,
    [string[]]$WorkspaceRoots,
    [string]$CodexExecutablePath
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $packageRoot "src\PocketConsole.Api"
$applicationExecutable = Join-Path $appDirectory "PocketConsole.Api.exe"
$applicationDll = Join-Path $appDirectory "PocketConsole.Api.dll"
$runtimeDirectory = Join-Path $packageRoot ".runtime"
$dataDirectory = Join-Path $runtimeDirectory "data"
$uploadDirectory = Join-Path $runtimeDirectory "uploads"
$pidFile = Join-Path $runtimeDirectory "pocket-console.pid"
$stdoutFile = Join-Path $runtimeDirectory "pocket-console.out.log"
$stderrFile = Join-Path $runtimeDirectory "pocket-console.err.log"
$passwordFile = Join-Path $runtimeDirectory "access-password.txt"
$workspaceConfig = Join-Path $packageRoot "config\workspace-roots.txt"
$codexConfig = Join-Path $packageRoot "config\codex-path.txt"

if (-not (Test-Path -LiteralPath $appDirectory)) {
    throw "Published application directory is missing: $appDirectory"
}

New-Item -ItemType Directory -Force -Path $runtimeDirectory, $dataDirectory, $uploadDirectory | Out-Null

function Get-ConfiguredLines {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(
        Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith("#") }
    )
}

function Resolve-ConfiguredPath {
    param([string]$Value)

    $expanded = [Environment]::ExpandEnvironmentVariables($Value.Trim())
    if (-not [System.IO.Path]::IsPathRooted($expanded)) {
        $expanded = Join-Path $packageRoot $expanded
    }

    try {
        return (Resolve-Path -LiteralPath $expanded -ErrorAction Stop).Path
    }
    catch {
        return [System.IO.Path]::GetFullPath($expanded)
    }
}

if ([string]::IsNullOrWhiteSpace($Password) -and (Test-Path -LiteralPath $passwordFile)) {
    $Password = (Get-Content -LiteralPath $passwordFile -Raw).Trim()
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $Password = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

Set-Content -LiteralPath $passwordFile -Value $Password -NoNewline

$configuredRoots = @()
if ($WorkspaceRoots -and $WorkspaceRoots.Count -gt 0) {
    $configuredRoots = @($WorkspaceRoots)
}
else {
    $configuredRoots = @(Get-ConfiguredLines $workspaceConfig)
}

$resolvedRoots = @()
foreach ($rootValue in $configuredRoots) {
    if ([string]::IsNullOrWhiteSpace($rootValue)) {
        continue
    }

    $resolvedRoot = Resolve-ConfiguredPath $rootValue
    if (-not ($resolvedRoots -contains $resolvedRoot)) {
        $resolvedRoots += $resolvedRoot
    }
}

if ($resolvedRoots.Count -eq 0) {
    $resolvedRoots = @($packageRoot)
}

$environmentKeys = @([Environment]::GetEnvironmentVariables().Keys | ForEach-Object { "$_" } | Where-Object { $_ -like "Security__WorkspaceRoots__*" })
foreach ($environmentKey in $environmentKeys) {
    Remove-Item -Path "env:$environmentKey" -ErrorAction SilentlyContinue
}

for ($index = 0; $index -lt $resolvedRoots.Count; $index++) {
    Set-Item -Path "env:Security__WorkspaceRoots__$index" -Value $resolvedRoots[$index]
}

$env:Security__Password = $Password
$env:ConnectionStrings__PocketConsole = "Data Source=$([System.IO.Path]::Combine($dataDirectory, 'pocket-console.db'))"
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:DOTNET_ENVIRONMENT = "Production"

if ([string]::IsNullOrWhiteSpace($CodexExecutablePath)) {
    $codexValues = @(Get-ConfiguredLines $codexConfig)
    if ($codexValues.Count -gt 0) {
        $CodexExecutablePath = $codexValues[0]
    }
}

if (-not [string]::IsNullOrWhiteSpace($CodexExecutablePath)) {
    $env:Codex__ExecutablePath = Resolve-ConfiguredPath $CodexExecutablePath
}
else {
    Remove-Item -Path "env:Codex__ExecutablePath" -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $pidFile) {
    $existingPid = (Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
    $existingProcess = $null
    if ($existingPid -match '^\d+$') {
        $existingProcess = Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue
    }

    $isHealthy = $false
    if ($existingProcess) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/auth/status" -UseBasicParsing -TimeoutSec 2
            $isHealthy = $response.StatusCode -eq 200
        }
        catch {
            $isHealthy = $false
        }
    }

    if ($isHealthy) {
        Write-Host "Pocket Console is already running (PID $existingPid)."
        Write-Host "Local URL: http://127.0.0.1:$Port"
        exit 0
    }

    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $applicationExecutable) {
    $startFilePath = $applicationExecutable
    $startArguments = @("--urls", "http://127.0.0.1:$Port")
}
elseif (Test-Path -LiteralPath $applicationDll) {
    $startFilePath = "dotnet"
    $startArguments = @($applicationDll, "--urls", "http://127.0.0.1:$Port")
}
else {
    throw "Published application is missing: $applicationExecutable"
}

$startOptions = @{
    FilePath = $startFilePath
    ArgumentList = $startArguments
    WorkingDirectory = $appDirectory
    RedirectStandardOutput = $stdoutFile
    RedirectStandardError = $stderrFile
    WindowStyle = "Hidden"
    PassThru = $true
}

$process = Start-Process @startOptions
Set-Content -LiteralPath $pidFile -Value $process.Id -NoNewline

$isHealthy = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        $errorText = if (Test-Path -LiteralPath $stderrFile) { Get-Content -LiteralPath $stderrFile -Raw } else { "" }
        throw "Pocket Console failed to start. $errorText"
    }

    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/auth/status" -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            $isHealthy = $true
            break
        }
    }
    catch {
    }

    Start-Sleep -Milliseconds 500
}

if (-not $isHealthy) {
    $errorText = if (Test-Path -LiteralPath $stderrFile) { Get-Content -LiteralPath $stderrFile -Raw } else { "" }
    throw "Pocket Console did not become ready. Check $stderrFile. $errorText"
}

Write-Host "Pocket Console started (PID $($process.Id))."
Write-Host "Local URL: http://127.0.0.1:$Port"
Write-Host "Access password: $Password"
Write-Host "Password file: $passwordFile"
Write-Host "Workspace roots: $($resolvedRoots -join '; ')"
Write-Host "For phone access, run scripts\Enable-Tailscale.ps1 after Tailscale is connected."

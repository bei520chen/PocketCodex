param(
    [string]$OutputDirectory,
    [string]$PackageName = "PocketConsole-win-x64",
    [string]$RuntimeIdentifier = "win-x64",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipFrontendBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "dist"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $OutputDirectory $PackageName))
$stagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $OutputDirectory ".staging-$PackageName"))
$zipPath = [System.IO.Path]::GetFullPath((Join-Path $OutputDirectory "$PackageName.zip"))

function Assert-ChildPath {
    param([string]$Path, [string]$Parent)

    $normalizedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $normalizedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $normalizedPath.StartsWith($normalizedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the output directory: $Path"
    }
}

function Remove-DirectoryIfPresent {
    param([string]$Path, [string]$Parent)

    Assert-ChildPath $Path $Parent
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Remove-DirectoryIfPresent $packageDirectory $OutputDirectory
Remove-DirectoryIfPresent $stagingDirectory $OutputDirectory
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found in PATH. Install the .NET 9 SDK first."
}
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "npm was not found in PATH. Install Node.js first."
}

$webDirectory = Join-Path $root "src\PocketConsole.Web"
$projectPath = Join-Path $root "src\PocketConsole.Api\PocketConsole.Api.csproj"
$publishDirectory = Join-Path $stagingDirectory "src\PocketConsole.Api"

if (-not $SkipFrontendBuild) {
    Push-Location $webDirectory
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $webDirectory "node_modules"))) {
            & npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) {
                throw "npm ci failed with exit code $LASTEXITCODE."
            }
        }

        & npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $root "src\PocketConsole.Api\wwwroot\index.html"))) {
    throw "Frontend assets are missing. Run the package script without -SkipFrontendBuild."
}

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publishDirectory `
    --nologo `
    "-p:WindowsAppSDKSelfContained=true" `
    "-p:PublishSingleFile=false" `
    "-p:PublishTrimmed=false" `
    "-p:DebugType=None" `
    "-p:DebugSymbols=false"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutable = Join-Path $publishDirectory "PocketConsole.Api.exe"
$publishedIndex = Join-Path $publishDirectory "wwwroot\index.html"
if (-not (Test-Path -LiteralPath $publishedExecutable)) {
    throw "Published executable is missing: $publishedExecutable"
}
if (-not (Test-Path -LiteralPath $publishedIndex)) {
    throw "Published frontend is missing: $publishedIndex"
}

$packageScriptsDirectory = Join-Path $stagingDirectory "scripts"
$configDirectory = Join-Path $stagingDirectory "config"
New-Item -ItemType Directory -Force -Path $packageScriptsDirectory, $configDirectory | Out-Null

Copy-Item (Join-Path $PSScriptRoot "Start-PocketConsole-Package.ps1") (Join-Path $packageScriptsDirectory "Start-PocketConsole.ps1")
Copy-Item (Join-Path $PSScriptRoot "Stop-PocketConsole-Package.ps1") (Join-Path $packageScriptsDirectory "Stop-PocketConsole.ps1")
Copy-Item (Join-Path $PSScriptRoot "Enable-Tailscale.ps1") (Join-Path $packageScriptsDirectory "Enable-Tailscale.ps1")
Copy-Item (Join-Path $PSScriptRoot "Disable-Tailscale.ps1") (Join-Path $packageScriptsDirectory "Disable-Tailscale.ps1")
Copy-Item (Join-Path $PSScriptRoot "Start-PocketConsole.cmd") (Join-Path $stagingDirectory "Start-PocketConsole.cmd")
Copy-Item (Join-Path $PSScriptRoot "Stop-PocketConsole.cmd") (Join-Path $stagingDirectory "Stop-PocketConsole.cmd")
Copy-Item (Join-Path $PSScriptRoot "Enable-Tailscale.cmd") (Join-Path $stagingDirectory "Enable-Tailscale.cmd")
Copy-Item (Join-Path $root "docs\PACKAGE-README.md") (Join-Path $stagingDirectory "README.md")

$workspaceTemplate = @('# Allowed workspace roots, one Windows path per line.', '# Example: D:\Projects', '# Leave empty to use the package directory.')
$codexTemplate = @('# Optional full path to codex.exe.', '# Leave empty to auto-detect Codex from the default location or PATH.')
Set-Content -LiteralPath (Join-Path $configDirectory "workspace-roots.txt") -Encoding UTF8 -Value $workspaceTemplate
Set-Content -LiteralPath (Join-Path $configDirectory "codex-path.txt") -Encoding UTF8 -Value $codexTemplate

$privateFile = Get-ChildItem -LiteralPath $stagingDirectory -Recurse -Force -File |
    Where-Object {
        $_.FullName -match '(^|[\\/])\.runtime([\\/]|$)' -or
        $_.Name -in @('access-password.txt', 'appsettings.Local.json', '.env', '.env.local') -or
        $_.Extension -in @('.db', '.sqlite', '.sqlite3', '.log') -or
        $_.Name -match '\.(db|sqlite|sqlite3)-(wal|shm)$'
    } |
    Select-Object -First 1
if ($privateFile) {
    throw "Refusing to package local data or credentials: $($privateFile.FullName)"
}

Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal
Move-Item -LiteralPath $stagingDirectory -Destination $packageDirectory

$zipSize = [Math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
Write-Host "Package created: $zipPath ($zipSize MB)"
Write-Host "Unpacked directory: $packageDirectory"

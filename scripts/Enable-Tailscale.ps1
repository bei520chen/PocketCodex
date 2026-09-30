param(
    [int]$Port = 5086,
    [switch]$Http
)

$ErrorActionPreference = "Stop"
$tailscaleCommand = Get-Command tailscale -ErrorAction SilentlyContinue
$tailscalePath = if ($tailscaleCommand) { $tailscaleCommand.Source } else { "C:\Program Files\Tailscale\tailscale.exe" }
if (-not (Test-Path -LiteralPath $tailscalePath)) {
    Write-Error "Tailscale is not installed. Install Tailscale on Windows and iPhone, then sign in to the same tailnet."
}

$status = & $tailscalePath status --json | ConvertFrom-Json
if ($status.BackendState -ne "Running") {
    Write-Error "Tailscale is not connected. Open Tailscale and sign in first."
}

if ($Http) {
    & $tailscalePath serve --bg --yes --http=80 $Port
    Write-Host "Tailscale private HTTP access is enabled."
    & $tailscalePath serve status
    exit 0
}

if (-not $status.CertDomains -or $status.CertDomains.Count -eq 0) {
    $enableUrl = "https://login.tailscale.com/f/serve?node=$($status.Self.ID)"
    Write-Host "Tailscale HTTPS Serve needs one-time tailnet approval."
    Write-Host "Open this URL, enable Serve, then rerun this script:"
    Write-Host $enableUrl
    Write-Host "Temporary tailnet-only HTTP fallback: .\scripts\Enable-Tailscale.ps1 -Http"
    exit 2
}

& $tailscalePath serve --bg --yes $Port
Write-Host "Tailscale HTTPS access is enabled."
& $tailscalePath serve status

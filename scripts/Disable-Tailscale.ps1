$ErrorActionPreference = "Stop"
$tailscaleCommand = Get-Command tailscale -ErrorAction SilentlyContinue
$tailscalePath = if ($tailscaleCommand) { $tailscaleCommand.Source } else { "C:\Program Files\Tailscale\tailscale.exe" }
if (-not (Test-Path -LiteralPath $tailscalePath)) {
    Write-Error "Tailscale is not installed."
}

& $tailscalePath serve reset
Write-Host "Tailscale Serve configuration was reset."

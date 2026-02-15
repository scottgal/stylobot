# run-local.ps1 — Start Gateway + Website locally (no Docker)
#
# Architecture:
#   Browser → Gateway (localhost:5010, bot detection) → Website (localhost:5062)
#
# Usage:
#   .\run-local.ps1           # Start both
#   .\run-local.ps1 -Gateway  # Gateway only
#   .\run-local.ps1 -Website  # Website only
#
# Then visit: http://localhost:5010
# Dashboard:  http://localhost:5062/_stylobot (direct, or via gateway)

param(
    [switch]$Gateway,
    [switch]$Website
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# If neither flag, start both
if (-not $Gateway -and -not $Website) {
    $Gateway = $true
    $Website = $true
}

$jobs = @()

if ($Website) {
    Write-Host "[Website] Starting on http://localhost:5062 ..." -ForegroundColor Cyan
    $jobs += Start-Job -Name "Website" -ScriptBlock {
        param($root)
        Set-Location "$root\mostlylucid.stylobot.website\src\Stylobot.Website"
        & dotnet run --launch-profile http 2>&1
    } -ArgumentList $root
}

if ($Gateway) {
    # Small delay so website is listening before gateway tries upstream health check
    if ($Website) { Start-Sleep -Seconds 2 }

    Write-Host "[Gateway] Starting on http://localhost:5010 → upstream http://localhost:5062 ..." -ForegroundColor Green
    $jobs += Start-Job -Name "Gateway" -ScriptBlock {
        param($root)
        Set-Location "$root\Stylobot.Gateway"
        $env:DEFAULT_UPSTREAM = "http://localhost:5062"
        & dotnet run --launch-profile "Gateway (Local Dev)" 2>&1
    } -ArgumentList $root
}

Write-Host ""
Write-Host "  Site via gateway:  http://localhost:5010" -ForegroundColor Yellow
Write-Host "  Site direct:       http://localhost:5062" -ForegroundColor Yellow
Write-Host "  Dashboard:         http://localhost:5062/_stylobot" -ForegroundColor Yellow
Write-Host ""
Write-Host "Press Ctrl+C to stop all." -ForegroundColor DarkGray
Write-Host ""

try {
    # Stream output from both jobs
    while ($true) {
        foreach ($job in $jobs) {
            $output = Receive-Job -Job $job -ErrorAction SilentlyContinue
            if ($output) {
                $prefix = if ($job.Name -eq "Gateway") { "[GW]" } else { "[WS]" }
                $color = if ($job.Name -eq "Gateway") { "Green" } else { "Cyan" }
                foreach ($line in $output) {
                    Write-Host "$prefix $line" -ForegroundColor $color
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
}
finally {
    Write-Host "`nStopping..." -ForegroundColor Red
    $jobs | Stop-Job -PassThru | Remove-Job -Force
}

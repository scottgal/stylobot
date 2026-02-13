#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Automated load testing script for bot detection gateway

.DESCRIPTION
    This script:
    1. Starts the test site (localhost:7240)
    2. Starts the gateway (localhost:5080)
    3. Converts signatures to k6 script
    4. Runs the load test
    5. Cleans up when done

.PARAMETER SignatureFile
    Path to the signature JSONL file (default: most recent signatures-*.jsonl)

.PARAMETER VUs
    Number of virtual users (default: 10)

.PARAMETER Duration
    Test duration (default: 30s)

.PARAMETER Mode
    Gateway mode: demo or production (default: production)

.EXAMPLE
    .\run-load-test.ps1

.EXAMPLE
    .\run-load-test.ps1 -SignatureFile signatures-2025-12-12.jsonl -VUs 20 -Duration 60s
#>

param(
    [string]$SignatureFile = "",
    [int]$VUs = 10,
    [string]$Duration = "30s",
    [string]$Mode = "production"
)

$ErrorActionPreference = "Stop"

# Find most recent signature file if not specified
if ([string]::IsNullOrEmpty($SignatureFile)) {
    $SignatureFile = Get-ChildItem "signatures-*.jsonl" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty Name
    if ([string]::IsNullOrEmpty($SignatureFile)) {
        Write-Error "No signature files found. Please specify -SignatureFile or create signatures first."
        exit 1
    }
    Write-Host "Using most recent signature file: $SignatureFile" -ForegroundColor Green
}

if (-not (Test-Path $SignatureFile)) {
    Write-Error "Signature file not found: $SignatureFile"
    exit 1
}

# Check if k6 is installed
if (-not (Get-Command "k6" -ErrorAction SilentlyContinue)) {
    Write-Error "k6 is not installed. Install from: https://k6.io/docs/get-started/installation/"
    Write-Host "  Windows: choco install k6" -ForegroundColor Yellow
    Write-Host "  Or download from: https://github.com/grafana/k6/releases" -ForegroundColor Yellow
    exit 1
}

# Check if dotnet-script is installed
if (-not (Get-Command "dotnet-script" -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet-script is not installed."
    Write-Host "Install with: dotnet tool install -g dotnet-script" -ForegroundColor Yellow
    exit 1
}

Write-Host "`n=== Bot Detection Load Test ===" -ForegroundColor Cyan
Write-Host "Signature file: $SignatureFile" -ForegroundColor White
Write-Host "VUs: $VUs, Duration: $Duration, Mode: $Mode`n" -ForegroundColor White

# Job tracking
$jobs = @()

try {
    # Step 1: Start test site
    Write-Host "[1/5] Starting test site on http://localhost:7240..." -ForegroundColor Yellow
    $testSiteJob = Start-Job -ScriptBlock {
        Set-Location $using:PWD
        Set-Location TestSite
        dotnet run
    }
    $jobs += $testSiteJob
    Start-Sleep -Seconds 3

    # Check if test site started
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:7240/health" -TimeoutSec 2 -ErrorAction Stop
        Write-Host "  ✓ Test site running" -ForegroundColor Green
    } catch {
        Write-Error "Test site failed to start. Check TestSite project."
        throw
    }

    # Step 2: Start gateway
    Write-Host "[2/5] Starting gateway on http://localhost:5080..." -ForegroundColor Yellow
    $gatewayJob = Start-Job -ScriptBlock {
        Set-Location $using:PWD
        Set-Location Mostlylucid.BotDetection.Console
        dotnet run -- --mode $using:Mode --upstream http://localhost:7240 --port 5080
    }
    $jobs += $gatewayJob
    Start-Sleep -Seconds 5

    # Check if gateway started
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:5080/health" -TimeoutSec 2 -ErrorAction Stop
        Write-Host "  ✓ Gateway running" -ForegroundColor Green
    } catch {
        Write-Error "Gateway failed to start. Check console app."
        throw
    }

    # Step 3: Convert signatures to k6 script
    Write-Host "[3/5] Converting signatures to k6 script..." -ForegroundColor Yellow
    $k6Script = "load-test-$(Get-Date -Format 'yyyy-MM-dd-HHmmss').js"
    dotnet script convert-signatures-to-k6.csx -- $SignatureFile $k6Script
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to convert signatures"
        exit 1
    }
    Write-Host "  ✓ Generated $k6Script" -ForegroundColor Green

    # Step 4: Run k6 load test
    Write-Host "`n[4/5] Running k6 load test...`n" -ForegroundColor Yellow
    k6 run --vus $VUs --duration $Duration $k6Script

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "k6 test completed with errors (exit code: $LASTEXITCODE)"
    } else {
        Write-Host "`n  ✓ Load test completed successfully" -ForegroundColor Green
    }

    # Step 5: Show results location
    Write-Host "`n[5/5] Results:" -ForegroundColor Yellow
    Write-Host "  - k6 script: $k6Script" -ForegroundColor White
    Write-Host "  - Gateway logs: Check console output or logs folder" -ForegroundColor White
    Write-Host "  - Signature files: signatures-*.jsonl" -ForegroundColor White

} catch {
    Write-Error "Load test failed: $_"
    exit 1
} finally {
    # Cleanup: Stop all background jobs
    Write-Host "`n=== Cleanup ===" -ForegroundColor Cyan
    foreach ($job in $jobs) {
        if ($job.State -eq "Running") {
            Write-Host "Stopping job: $($job.Name)..." -ForegroundColor Yellow
            Stop-Job $job
            Remove-Job $job -Force
        }
    }
    Write-Host "✓ All jobs stopped" -ForegroundColor Green
}

Write-Host "`n=== Load Test Complete ===" -ForegroundColor Cyan

# Test Demo Loop - YARP Gateway -> Backend
# This script verifies the complete demo flow

Write-Host "=== Bot Detection Demo Loop Test ===" -ForegroundColor Cyan
Write-Host ""

# Check if ports are available
$demoPort = 5000
$gatewayPort = 5100

Write-Host "Checking port availability..." -ForegroundColor Yellow
$demoBusy = Get-NetTCPConnection -LocalPort $demoPort -ErrorAction SilentlyContinue
$gatewayBusy = Get-NetTCPConnection -LocalPort $gatewayPort -ErrorAction SilentlyContinue

if ($demoBusy) {
    Write-Host "⚠️  Port $demoPort is already in use. Stop the process or choose a different port." -ForegroundColor Red
    exit 1
}

if ($gatewayBusy) {
    Write-Host "⚠️  Port $gatewayPort is already in use. Stop the process or choose a different port." -ForegroundColor Red
    exit 1
}

Write-Host "✅ Ports are available" -ForegroundColor Green
Write-Host ""

# Build the projects
Write-Host "Building Demo App..." -ForegroundColor Yellow
dotnet build Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Demo build failed" -ForegroundColor Red
    exit 1
}
Write-Host "✅ Demo app built successfully" -ForegroundColor Green

Write-Host "Building Console Gateway..." -ForegroundColor Yellow
dotnet build Mostlylucid.BotDetection.Console/Mostlylucid.BotDetection.Console.csproj -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Gateway build failed" -ForegroundColor Red
    exit 1
}
Write-Host "✅ Console gateway built successfully" -ForegroundColor Green
Write-Host ""

# Start the demo app
Write-Host "Starting Demo App on http://localhost:$demoPort..." -ForegroundColor Yellow
$demoJob = Start-Job -ScriptBlock {
    param($port)
    Set-Location $using:PWD
    dotnet run --project Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj --no-build --urls "http://localhost:$port" 2>&1
} -ArgumentList $demoPort

Start-Sleep -Seconds 3

# Check if demo started
$demoRunning = Get-NetTCPConnection -LocalPort $demoPort -ErrorAction SilentlyContinue
if (-not $demoRunning) {
    Write-Host "❌ Demo app failed to start" -ForegroundColor Red
    Stop-Job -Job $demoJob -ErrorAction SilentlyContinue
    Remove-Job -Job $demoJob -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "✅ Demo app started on http://localhost:$demoPort" -ForegroundColor Green
Write-Host ""

# Start the gateway
Write-Host "Starting YARP Gateway on http://localhost:$gatewayPort..." -ForegroundColor Yellow
$gatewayJob = Start-Job -ScriptBlock {
    param($port, $upstream)
    Set-Location $using:PWD
    dotnet run --project Mostlylucid.BotDetection.Console/Mostlylucid.BotDetection.Console.csproj --no-build -- --upstream "http://localhost:$upstream" --port $port --mode demo 2>&1
} -ArgumentList $gatewayPort, $demoPort

Start-Sleep -Seconds 3

# Check if gateway started
$gatewayRunning = Get-NetTCPConnection -LocalPort $gatewayPort -ErrorAction SilentlyContinue
if (-not $gatewayRunning) {
    Write-Host "❌ Gateway failed to start" -ForegroundColor Red
    Stop-Job -Job $demoJob,$gatewayJob -ErrorAction SilentlyContinue
    Remove-Job -Job $demoJob,$gatewayJob -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "✅ Gateway started on http://localhost:$gatewayPort" -ForegroundColor Green
Write-Host ""

# Test the endpoints
Write-Host "Testing Demo Page (Direct Access)..." -ForegroundColor Yellow
try {
    $directResponse = Invoke-WebRequest -Uri "http://localhost:$demoPort/YarpProxyDemo" -UseBasicParsing -TimeoutSec 10
    if ($directResponse.StatusCode -eq 200) {
        Write-Host "✅ Direct access works (200 OK)" -ForegroundColor Green
        if ($directResponse.Content -like "*Bot Detection Details*") {
            Write-Host "✅ Page contains detection details" -ForegroundColor Green
        } else {
            Write-Host "⚠️  Page loaded but doesn't contain detection details" -ForegroundColor Yellow
        }
    }
} catch {
    Write-Host "❌ Direct access failed: $_" -ForegroundColor Red
}
Write-Host ""

Write-Host "Testing Demo Page (Via Gateway)..." -ForegroundColor Yellow
try {
    $gatewayResponse = Invoke-WebRequest -Uri "http://localhost:$gatewayPort/YarpProxyDemo" -UseBasicParsing -TimeoutSec 10
    if ($gatewayResponse.StatusCode -eq 200) {
        Write-Host "✅ Gateway access works (200 OK)" -ForegroundColor Green

        # Check for YARP headers
        $hasDetectionHeader = $gatewayResponse.Headers.ContainsKey("X-Bot-Detection-Result") -or
                              $gatewayResponse.Headers.ContainsKey("X-Bot-Detection-Probability")

        if ($gatewayResponse.Content -like "*Bot Detection Details*") {
            Write-Host "✅ Page contains detection details" -ForegroundColor Green
        }

        if ($gatewayResponse.Content -like "*Behind YARP Proxy*" -or $gatewayResponse.Content -like "*YARP Proxy*") {
            Write-Host "✅ Page indicates YARP proxy mode" -ForegroundColor Green
        }
    }
} catch {
    Write-Host "❌ Gateway access failed: $_" -ForegroundColor Red
}
Write-Host ""

# Test with different user agents
Write-Host "Testing with Bot User-Agent (Scrapy)..." -ForegroundColor Yellow
try {
    $botResponse = Invoke-WebRequest -Uri "http://localhost:$gatewayPort/YarpProxyDemo" `
        -UserAgent "Scrapy/2.5.0" `
        -UseBasicParsing `
        -TimeoutSec 10

    if ($botResponse.StatusCode -eq 200) {
        Write-Host "✅ Bot request succeeded (200 OK)" -ForegroundColor Green
    }
} catch {
    Write-Host "⚠️  Bot request failed (might be blocked): $_" -ForegroundColor Yellow
}
Write-Host ""

# Cleanup
Write-Host "Cleaning up..." -ForegroundColor Yellow
Stop-Job -Job $demoJob,$gatewayJob -ErrorAction SilentlyContinue
Remove-Job -Job $demoJob,$gatewayJob -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "=== Test Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "To manually test:" -ForegroundColor Yellow
Write-Host "1. Terminal 1: cd Mostlylucid.BotDetection.Demo && dotnet run" -ForegroundColor White
Write-Host "2. Terminal 2: cd Mostlylucid.BotDetection.Console && dotnet run -- --upstream http://localhost:5000 --port 5100 --mode demo" -ForegroundColor White
Write-Host "3. Browser: http://localhost:5100/YarpProxyDemo" -ForegroundColor White
Write-Host ""

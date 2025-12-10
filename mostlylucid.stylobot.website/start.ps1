#!/usr/bin/env pwsh
# Quick start script for StyloBot website

Write-Host "🚀 Starting StyloBot Website with YARP Gateway..." -ForegroundColor Cyan
Write-Host ""

# Check if Docker is running
$dockerRunning = docker ps 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Docker is not running. Please start Docker Desktop." -ForegroundColor Red
    exit 1
}

# Stop any existing containers
Write-Host "🛑 Stopping existing containers..." -ForegroundColor Yellow
docker-compose down 2>&1 | Out-Null

# Start services
Write-Host "🏗️  Building and starting services..." -ForegroundColor Green
docker-compose up -d --build

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "✅ Services started successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "📍 Access the site at:" -ForegroundColor Cyan
    Write-Host "   - http://localhost" -ForegroundColor White
    Write-Host "   - https://localhost (if SSL configured)" -ForegroundColor White
    Write-Host ""
    Write-Host "🔍 View logs:" -ForegroundColor Cyan
    Write-Host "   docker-compose logs -f" -ForegroundColor White
    Write-Host ""
    Write-Host "🛑 Stop services:" -ForegroundColor Cyan
    Write-Host "   docker-compose down" -ForegroundColor White
    Write-Host ""
    Write-Host "🤖 Bot detection is active via YARP Gateway!" -ForegroundColor Magenta
    Write-Host "   Every request is analyzed and learned automatically." -ForegroundColor Gray
} else {
    Write-Host ""
    Write-Host "❌ Failed to start services. Check the logs:" -ForegroundColor Red
    Write-Host "   docker-compose logs" -ForegroundColor White
    exit 1
}

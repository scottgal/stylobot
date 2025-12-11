#!/usr/bin/env pwsh
# PowerShell script to build Docker image for Stylobot Website

param(
    [string]$Tag = "latest",
    [string]$ImageName = "stylobot-website",
    [switch]$NoCache,
    [switch]$SaveTarball,
    [switch]$Compress
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building Stylobot Website Docker Image" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get script directory (project root)
$ProjectRoot = $PSScriptRoot
Write-Host "Project Root: $ProjectRoot" -ForegroundColor Yellow

# Full image tag
$FullTag = "${ImageName}:${Tag}"
Write-Host "Building: $FullTag" -ForegroundColor Green
Write-Host ""

# Build arguments
$BuildArgs = @("build", "-t", $FullTag, "-f", "Dockerfile", ".")

if ($NoCache) {
    Write-Host "Building with --no-cache flag" -ForegroundColor Yellow
    $BuildArgs += "--no-cache"
}

# Build the Docker image
Write-Host "Running: docker $($BuildArgs -join ' ')" -ForegroundColor Cyan
Set-Location $ProjectRoot

try {
    & docker $BuildArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Docker build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Build Successful!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Image: $FullTag" -ForegroundColor Cyan

    # Get image info
    $ImageInfo = docker images $FullTag --format "{{.Size}}"
    Write-Host "Size: $ImageInfo" -ForegroundColor Cyan
    Write-Host ""

    # Save as tarball if requested
    if ($SaveTarball -or $Compress) {
        $OutputDir = Join-Path $ProjectRoot "dist"
        if (-not (Test-Path $OutputDir)) {
            New-Item -ItemType Directory -Path $OutputDir | Out-Null
        }

        $TarFile = Join-Path $OutputDir "${ImageName}-${Tag}.tar"

        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "Saving Docker Image as Tarball" -ForegroundColor Cyan
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Saving to: $TarFile" -ForegroundColor Yellow

        & docker save -o $TarFile $FullTag

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Docker save failed with exit code $LASTEXITCODE" -ForegroundColor Red
            exit $LASTEXITCODE
        }

        $TarSize = (Get-Item $TarFile).Length / 1MB
        Write-Host "Tarball created: $([math]::Round($TarSize, 2)) MB" -ForegroundColor Green

        # Compress if requested
        if ($Compress) {
            Write-Host ""
            Write-Host "Compressing tarball with gzip..." -ForegroundColor Cyan

            $GzFile = "$TarFile.gz"

            # Use native gzip if available, otherwise use .NET compression
            $gzipPath = Get-Command gzip -ErrorAction SilentlyContinue

            if ($gzipPath) {
                & gzip -f $TarFile
                $GzFile = "$TarFile.gz"
            } else {
                # Use .NET compression as fallback
                Write-Host "Using .NET compression (gzip not found in PATH)..." -ForegroundColor Yellow

                Add-Type -AssemblyName System.IO.Compression.FileSystem

                $sourceStream = [System.IO.File]::OpenRead($TarFile)
                $destStream = [System.IO.File]::Create($GzFile)
                $gzipStream = New-Object System.IO.Compression.GZipStream($destStream, [System.IO.Compression.CompressionMode]::Compress)

                $sourceStream.CopyTo($gzipStream)

                $gzipStream.Close()
                $destStream.Close()
                $sourceStream.Close()

                # Remove original tar file
                Remove-Item $TarFile
            }

            if (Test-Path $GzFile) {
                $GzSize = (Get-Item $GzFile).Length / 1MB
                Write-Host "Compressed: $([math]::Round($GzSize, 2)) MB" -ForegroundColor Green
                Write-Host ""
                Write-Host "========================================" -ForegroundColor Green
                Write-Host "Compressed Image Ready for Deployment!" -ForegroundColor Green
                Write-Host "========================================" -ForegroundColor Green
                Write-Host ""
                Write-Host "File: $GzFile" -ForegroundColor Cyan
                Write-Host ""
                Write-Host "To deploy on remote server:" -ForegroundColor Yellow
                Write-Host "  1. Copy to server: scp $GzFile user@server:/tmp/" -ForegroundColor White
                Write-Host "  2. On server: gunzip -c /tmp/$(Split-Path $GzFile -Leaf) | docker load" -ForegroundColor White
                Write-Host "  3. Run: docker-compose up -d" -ForegroundColor White
            } else {
                Write-Host "Compression failed!" -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host ""
            Write-Host "To load on another machine:" -ForegroundColor Yellow
            Write-Host "  docker load -i $TarFile" -ForegroundColor White
        }
    }

    Write-Host ""
    Write-Host "To run locally:" -ForegroundColor Yellow
    Write-Host "  docker run -d -p 8080:8080 --name stylobot $FullTag" -ForegroundColor White
    Write-Host ""
    Write-Host "To run with docker-compose:" -ForegroundColor Yellow
    Write-Host "  docker-compose up -d" -ForegroundColor White
    Write-Host ""

} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    exit 1
}

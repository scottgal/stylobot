# Build script for Mostlylucid.BotDetection.Console
# Builds AOT-compiled single-file executables for multiple platforms

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('all', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Target = 'all',

    [Parameter(Mandatory = $false)]
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = "Stop"

Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   Mostlylucid Bot Detection Console - Build Script      ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

$projectPath = "Mostlylucid.BotDetection.Console.csproj"

function Build-Target
{
    param(
        [string]$RuntimeId,
        [string]$PlatformName
    )

    Write-Host "Building for $PlatformName ($RuntimeId)..." -ForegroundColor Yellow

    $outputPath = "bin/$Configuration/net9.0/$RuntimeId/publish"

    # Clean previous build
    if (Test-Path $outputPath)
    {
        Remove-Item $outputPath -Recurse -Force
    }

    # Build
    dotnet publish $projectPath `
        -c $Configuration `
        -r $RuntimeId `
        --self-contained `
        /p:PublishAot=true `
        /p:PublishTrimmed=true `
        /p:PublishSingleFile=true `
        /p:TrimMode=full `
        /p:StripSymbols=true `
        /p:OptimizationPreference=Speed `
        /p:IlcOptimizationPreference=Speed

    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "Build failed for $PlatformName" -ForegroundColor Red
        exit 1
    }

    # Get file size
    $exeName = if ( $RuntimeId.StartsWith("win"))
    {
        "minigw.exe"
    }
    else
    {
        "minigw"
    }
    $exePath = Join-Path $outputPath $exeName

    if (Test-Path $exePath)
    {
        $size = (Get-Item $exePath).Length / 1MB
        Write-Host "✓ Built successfully: $exePath ($([math]::Round($size, 2) ) MB)" -ForegroundColor Green
    }

    Write-Host ""
}

# Build targets
switch ($Target)
{
    'all' {
        Write-Host "Building all targets..." -ForegroundColor Cyan
        Write-Host ""
        Build-Target "win-x64" "Windows x64"
        Build-Target "win-arm64" "Windows ARM64 (Surface Pro X)"
        Build-Target "linux-x64" "Linux x64"
        Build-Target "linux-arm64" "Linux ARM64 (Raspberry Pi)"
        Build-Target "osx-x64" "macOS x64 (Intel)"
        Build-Target "osx-arm64" "macOS ARM64 (Apple Silicon)"
    }
    default {
        $platformName = switch ($Target)
        {
            'win-x64' {
                "Windows x64"
            }
            'win-arm64' {
                "Windows ARM64 (Surface Pro X)"
            }
            'linux-x64' {
                "Linux x64"
            }
            'linux-arm64' {
                "Linux ARM64 (Raspberry Pi)"
            }
            'osx-x64' {
                "macOS x64 (Intel)"
            }
            'osx-arm64' {
                "macOS ARM64 (Apple Silicon)"
            }
        }
        Build-Target $Target $platformName
    }
}

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "Executables are in bin/$Configuration/net9.0/{runtime}/publish/" -ForegroundColor Cyan
Write-Host ""
Write-Host "Example usage:" -ForegroundColor Yellow
Write-Host "  Windows:      .\minigw.exe --upstream http://backend:8080 --port 5080" -ForegroundColor Gray
Write-Host "  Linux/macOS:  ./minigw --upstream http://backend:8080 --port 5080" -ForegroundColor Gray
Write-Host ""

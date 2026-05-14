$ErrorActionPreference = 'Stop'

$packageName = 'stylobot'
$version = $env:chocolateyPackageVersion

# Architecture detection. RuntimeInformation.OSArchitecture is preferred over
# $env:PROCESSOR_ARCHITECTURE because the latter reports "AMD64" inside a
# WOW64 32-bit PowerShell on an ARM64 host. Install-ChocolateyZipPackage does
# not natively differentiate ARM64 from x64, so we must pick URL + checksum
# manually based on the detected arch.
$osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture

switch ($osArch) {
    'Arm64' {
        $arch     = 'win-arm64'
        $checksum = 'ARM64_CHECKSUM_PLACEHOLDER'
    }
    'X64' {
        $arch     = 'win-x64'
        $checksum = 'X64_CHECKSUM_PLACEHOLDER'
    }
    default {
        throw "StyloBot supports x64 and ARM64 Windows only. Detected OS architecture: $osArch"
    }
}

$url = "https://github.com/scottgal/stylobot/releases/download/allbot-v$version/stylobot-$arch.zip"

$installDir = Join-Path $env:ChocolateyInstall "lib\$packageName\tools"

$packageArgs = @{
    packageName   = $packageName
    url           = $url
    unzipLocation = $installDir
    checksum      = $checksum
    checksumType  = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs

# Create shim for the binary
$exePath = Join-Path $installDir 'stylobot.exe'
Install-BinFile -Name 'stylobot' -Path $exePath

Write-Host ""
Write-Host "StyloBot installed! Free forever." -ForegroundColor Green
Write-Host "  Architecture: $arch"
Write-Host "  stylobot 5080 http://localhost:3000"
Write-Host "  stylobot --help"
Write-Host ""

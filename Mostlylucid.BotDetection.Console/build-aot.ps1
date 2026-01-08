# Build script for AOT compilation
$ErrorActionPreference = "Stop"

# Add VS Installer to PATH
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"

# Initialize VS Developer environment
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64

# Navigate to project directory
Set-Location "D:\Source\mostlylucid.nugetpackages\Mostlylucid.BotDetection.Console"

# Build with AOT
dotnet publish -c Release -r win-x64 --self-contained

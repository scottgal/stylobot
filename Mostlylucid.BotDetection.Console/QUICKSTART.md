# Quick Start Guide

Get the minimal YARP gateway with bot detection running in 5 minutes.

## Option 1: Run from Source (Fastest)

```bash
# Navigate to project
cd Mostlylucid.BotDetection.Console

# Run in demo mode (default)
dotnet run -- --upstream http://localhost:8080 --port 5000

# Run in production mode
dotnet run -- --mode production --upstream http://localhost:8080 --port 80
```

## Option 2: Build and Run Executable

### Windows (PowerShell)

```powershell
# Build for current platform
cd Mostlylucid.BotDetection.Console
.\build.ps1 -Target win-x64

# Run
.\bin\Release\net9.0\win-x64\publish\minigw.exe --upstream http://localhost:8080 --port 5000
```

### Linux / macOS (Bash)

```bash
# Build for current platform
cd Mostlylucid.BotDetection.Console
./build.sh linux-x64  # or linux-arm64 for Raspberry Pi

# Run
chmod +x bin/Release/net9.0/linux-x64/publish/minigw
./bin/Release/net9.0/linux-x64/publish/minigw --upstream http://localhost:8080 --port 5000
```

## Build All Platforms

```powershell
# Windows
.\build.ps1 -Target all

# Linux/macOS
./build.sh all
```

This builds:

- Windows x64
- Linux x64
- Linux ARM64 (Raspberry Pi 4/5)
- macOS x64 (Intel)
- macOS ARM64 (Apple Silicon)

## Test It

```bash
# Health check
curl http://localhost:5000/health

# Test with human browser (should pass)
curl -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0" \
  http://localhost:5000/

# Test with bot (should detect)
curl -H "User-Agent: curl/8.4.0" \
  http://localhost:5000/
```

## Configuration

### Command-Line

```bash
minigw --upstream http://backend:8080 --port 5000 --mode demo
```

### Environment Variables

```bash
export UPSTREAM=http://backend:8080
export PORT=5000
export MODE=production

./minigw
```

### Config Files

Edit `appsettings.json` (demo mode) or `appsettings.production.json` (production mode).

## Demo vs Production

| Feature   | Demo Mode       | Production Mode                 |
|-----------|-----------------|---------------------------------|
| Blocking  | ❌ Disabled      | ✅ Enabled                       |
| Detectors | Fast-path only  | Fast + Slow + AI                |
| Learning  | ❌ Disabled      | ✅ Enabled                       |
| Logging   | 📊 Full verbose | 📝 Concise                      |
| Action    | Allow all       | Adaptive (block/throttle/allow) |

## Deploy to Raspberry Pi

```bash
# Build on your dev machine
./build.sh linux-arm64

# Copy to Pi
scp bin/Release/net9.0/linux-arm64/publish/minigw pi@raspberrypi.local:~/
scp appsettings*.json pi@raspberrypi.local:~/

# Run on Pi
ssh pi@raspberrypi.local
chmod +x minigw
./minigw --upstream http://localhost:8080 --port 5000
```

## Troubleshoot

### "Permission denied"

```bash
chmod +x minigw
```

### "Cannot execute binary file"

Wrong architecture. Rebuild for correct platform:

- Pi 4/5: `linux-arm64`
- x86_64 servers: `linux-x64`
- Windows: `win-x64`

### Port already in use

Change port: `--port 5001`

## Next Steps

- See `README.md` for full documentation
- Test with `test.http` file (VS Code REST Client)
- Check logs for detection details
- Tune configuration in `appsettings.json`

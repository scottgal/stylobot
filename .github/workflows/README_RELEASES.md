# GitHub Actions Release Workflows

This directory contains GitHub Actions workflows for building and publishing releases.

## Console Gateway Binary Releases

**Workflow:** `publish-console-gateway.yml`

### Automatic Trigger (Recommended)

Create a git tag to trigger automatic release:

```bash
# For production release
git tag console-v1.0.0
git push origin console-v1.0.0

# For preview/beta release
git tag console-v1.0.0-preview1
git push origin console-v1.0.0-preview1
```

This will:
1. Build native binaries for all platforms (Linux x64/ARM64, Windows, macOS Intel/ARM)
2. Create distribution packages with README and config files
3. Generate SHA256 checksums
4. Create a GitHub Release with all binaries attached
5. Mark as prerelease if version contains `-`

### Manual Trigger

You can also trigger releases manually from GitHub Actions UI:

1. Go to https://github.com/scottgal/mostlylucid.stylobot/actions
2. Select "Publish Console Gateway Binaries" workflow
3. Click "Run workflow"
4. Enter version (e.g., `1.0.0` or `1.0.0-preview1`)
5. Click "Run workflow"

### Built Artifacts

Each release includes:

- **Linux x64**: `stylobot-linux-x64.tar.gz` (~12MB)
- **Linux ARM64**: `stylobot-linux-arm64.tar.gz` (~11MB) - For Raspberry Pi 4/5
- **Windows x64**: `stylobot-win-x64.zip` (~10MB)
- **macOS Intel**: `stylobot-osx-x64.tar.gz` (~13MB)
- **macOS Apple Silicon**: `stylobot-osx-arm64.tar.gz` (~11MB)
- **Checksums**: `SHA256SUMS.txt`

Each archive contains:
- `stylobot` or `stylobot.exe` - The native executable
- `appsettings.json` - Default configuration
- `appsettings.production.json` - Production configuration
- `README.txt` - Quick start instructions

### Version Naming

Follow semantic versioning:

- **Major.Minor.Patch**: `console-v1.0.0`
- **Preview**: `console-v1.0.0-preview1`
- **Beta**: `console-v1.0.0-beta2`
- **Release Candidate**: `console-v1.0.0-rc1`

### Publishing Checklist

Before creating a release tag:

1. ✅ Update version in CHANGELOG or release notes
2. ✅ Ensure all tests pass: `dotnet test`
3. ✅ Verify builds locally: `dotnet build -c Release`
4. ✅ Test on target platforms if possible
5. ✅ Update documentation if needed
6. ✅ Commit and push all changes
7. ✅ Create and push git tag
8. ✅ Monitor GitHub Actions for build success
9. ✅ Verify release assets on GitHub Releases page
10. ✅ Download and test at least one binary

### Testing a Release Locally

Before publishing, you can test the build process locally:

```bash
# Build for your platform
dotnet publish Mostlylucid.BotDetection.Console/Mostlylucid.BotDetection.Console.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:PublishAot=true \
  -p:PublishTrimmed=true \
  -p:PublishSingleFile=true

# Test the binary
cd bin/Release/net10.0/linux-x64/publish
./stylobot --upstream http://localhost:8080 --port 5080
```

### Troubleshooting

**Build fails on specific platform:**
- Check GitHub Actions logs for errors
- Ensure .NET 10.0 SDK is properly configured
- Verify AOT compiler toolchain is available (may require OS-specific tools)

**Release not created:**
- Check GitHub Actions permissions (needs `contents: write`)
- Verify tag format matches workflow trigger patterns
- Check for errors in release notes generation

**Binary doesn't run:**
- Verify correct architecture (x64 vs ARM64)
- Check for missing native dependencies on target platform
- Ensure executable permissions on Linux/macOS: `chmod +x stylobot`

### All-in-One Release (All Bot Projects)

The `allbot-v*` tag pattern triggers releases for ALL bot detection projects:

```bash
git tag allbot-v1.0.0
git push origin allbot-v1.0.0
```

This will trigger:
- Console gateway binary release
- Stylobot Gateway Docker image
- BotDetection NuGet packages
- Any other configured workflows

Use this for coordinated releases across the entire bot detection suite.

## Stylobot Gateway Docker Release

**Workflow:** `publish-yarpgateway.yml`

Builds and publishes Docker images to Docker Hub.

### Trigger

```bash
# For Stylobot Gateway release
git tag gateway-v1.0.0
git push origin gateway-v1.0.0

# Or use all-in-one
git tag allbot-v1.0.0
git push origin allbot-v1.0.0
```

## Stylobot UI Docker Release

**Workflow:** `publish-stylobot-ui.yml`

Builds and publishes the dashboard-host image to Docker Hub
(`scottgal/stylobot-ui`). The UI image runs a pure REST + SignalR client
against a remote `stylobot` gateway started with `--enable-api`. Reads
nothing locally; designed to be hosted inside a network as a viewer.

### Trigger

```bash
# UI-only release
git tag ui-v1.0.0
git push origin ui-v1.0.0

# Or as part of the all-in-one allbot tag
git tag allbot-v1.0.0
git push origin allbot-v1.0.0
```

Not Native AOT (the dashboard pipeline needs Razor / SignalR / dynamic JSON
reflection); not packable as NuGet (ships as a container only).

## Stylobot All-in-One Docker Release

**Workflow:** `publish-stylobot-all.yml`

Builds and publishes the all-in-one image to Docker Hub
(`scottgal/stylobot-all`). YARP gateway + detection + dashboard in one
process. One container, simplest deployment.

### Trigger

```bash
# All-in-one-only release
git tag all-v1.0.0
git push origin all-v1.0.0

# Or as part of the all-in-one allbot tag
git tag allbot-v1.0.0
git push origin allbot-v1.0.0
```

Configure `ReverseProxy:Routes` in appsettings to drive upstream routing.
Not Native AOT for the same reason as `stylobot-ui`.

## StyloBot Sidecar Release

**Workflow:** `publish-sidecar.yml`

Publishes the detection sidecar two ways, as part of the main `allbot-v*` release:

1. A multi-arch Docker image to Docker Hub (`scottgal/stylobot-sidecar`)
2. Self-contained single-file binaries (Linux/Windows/macOS, x64 + ARM64)
   attached to the GitHub Release. The binary is one file with no runtime
   dependency, so it can run as the image or be `COPY`-d into your app's
   container to co-locate the sidecar with your app.

### Trigger

```bash
# As part of the all-in-one release
git tag allbot-v1.0.0
git push origin allbot-v1.0.0

# Or sidecar-only
git tag sidecar-v1.0.0
git push origin sidecar-v1.0.0
```

The binaries are **self-contained but not Native AOT** (the gRPC server and the
Fluid template engine are not guaranteed AOT-clean), so they are larger than the
AOT Console binaries but build reliably on every platform.

## NuGet Package Releases

**Workflow:** `publish-botdetection.yml`, `publish-geodetection.yml`, etc.

Publishes NuGet packages to nuget.org.

See individual workflow files for tag patterns and configuration.

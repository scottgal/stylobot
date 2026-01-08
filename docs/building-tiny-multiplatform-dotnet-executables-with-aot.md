# Building Tiny Multi-Platform .NET Executables with Native AOT

If you've ever wanted to ship a .NET application as a single, native executable that starts instantly and runs anywhere—without requiring users to install .NET—then Native AOT (Ahead-of-Time compilation) is your answer. In this comprehensive guide, I'll walk you through building multi-platform native executables that are genuinely tiny (10-30MB), run on Windows, Linux (including ARM64 Raspberry Pi), and macOS, and deploy automatically via GitHub Actions.

## Table of Contents

1. [What is Native AOT and Why Should You Care?](#what-is-native-aot)
2. [The Trade-offs](#the-tradeoffs)
3. [Project Setup for AOT](#project-setup)
4. [The SQLite Problem (and Solution)](#sqlite-problem)
5. [Multi-Platform GitHub Actions Configuration](#github-actions)
6. [Common Pitfalls and Solutions](#pitfalls)
7. [Real-World Results](#results)

<a name="what-is-native-aot"></a>
## What is Native AOT and Why Should You Care?

Native AOT compiles your .NET application directly to native machine code **at build time** instead of relying on the JIT (Just-In-Time) compiler at runtime. This means:

### Benefits

- **Zero dependencies**: No .NET runtime required on target machines
- **Instant startup**: No JIT warm-up time (typically 50-90% faster cold starts)
- **Tiny footprint**: 10-30MB instead of 50-150MB self-contained deployments
- **Lower memory usage**: No JIT compiler in memory, tighter code
- **Better performance**: Optimized native code from the start

### The Ideal Use Cases

- **CLI tools and utilities**: Fast startup matters
- **Containers and microservices**: Smaller images, faster scaling
- **Edge devices**: Limited resources (IoT, Raspberry Pi)
- **Gateway/proxy applications**: High throughput, low latency
- **Lambda/serverless**: Cold start times are critical

<a name="the-tradeoffs"></a>
## The Trade-offs

AOT isn't magic. Here's what you give up:

### 1. No Dynamic Code Generation

AOT can't support:
- `System.Reflection.Emit`
- Dynamic assembly loading
- Runtime code generation

**Impact**: Frameworks like Entity Framework Core need special configuration. Heavy reflection-based code may not work.

### 2. Trimming Warnings

The AOT compiler aggressively trims unused code. If you use reflection, JSON serialization, or dynamic features, you'll get warnings (and sometimes runtime failures) if types are trimmed away.

**Solution**: Use source generators (like `System.Text.Json` source generation) or mark types with `[DynamicallyAccessedMembers]` attributes.

### 3. Longer Build Times

Native compilation takes longer than normal .NET builds—expect 2-5 minutes instead of seconds.

### 4. Platform-Specific Builds

You must build **one binary per platform**. A Windows AOT binary won't run on Linux.

**Solution**: Use GitHub Actions matrix builds (we'll cover this).

<a name="project-setup"></a>
## Project Setup for AOT

Here's a complete `.csproj` configured for multi-platform AOT:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <!-- Native AOT -->
    <PublishAot>true</PublishAot>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>full</TrimMode>
    <InvariantGlobalization>true</InvariantGlobalization>

    <!-- Single File -->
    <PublishSingleFile>true</PublishSingleFile>
    <StripSymbols>true</StripSymbols>

    <!-- Optimization -->
    <OptimizationPreference>Speed</OptimizationPreference>
    <IlcOptimizationPreference>Speed</IlcOptimizationPreference>

    <!-- Version -->
    <Version>1.0.0</Version>
    <AssemblyName>myapp</AssemblyName>

    <!-- Declare all target platforms -->
    <RuntimeIdentifiers>
      win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64
    </RuntimeIdentifiers>
  </PropertyGroup>

  <ItemGroup>
    <!-- Your dependencies here -->
  </ItemGroup>

</Project>
```

### Key Settings Explained

#### `PublishAot=true`
Enables Native AOT compilation. This is the master switch.

#### `PublishTrimmed=true` and `TrimMode=full`
Aggressively removes unused code. `full` mode trims more than `partial` but requires careful testing.

#### `InvariantGlobalization=true`
Removes culture-specific data (saves ~5-10MB). Only use if you don't need localization, date/time formatting for specific cultures, or string comparison rules beyond ordinal.

**Skip this if you need:**
- Non-English date/time formatting
- Culture-specific number formatting
- Case-insensitive string comparisons in non-English text

#### `StripSymbols=true`
Removes debug symbols from the final binary (saves MB).

#### `PublishSingleFile=true`
Bundles everything into one executable (except native dependencies like SQLite—more on that below).

#### `OptimizationPreference=Speed`
Tells the AOT compiler to optimize for performance over size. Use `Size` if you need the smallest possible binary.

#### `RuntimeIdentifiers`
Declares which platforms you support. This doesn't build them all—it just tells tooling they're valid targets.

<a name="sqlite-problem"></a>
## The SQLite Problem (and Solution)

SQLite is **the** most common pain point for AOT beginners. Here's why and how to fix it.

### The Problem

`Microsoft.Data.Sqlite` has transitive dependencies that pull in incompatible SQLite providers for AOT. By default, you'll hit runtime errors like:

```
DllNotFoundException: Unable to load DLL 'e_sqlite3' or one of its dependencies
```

This happens because:
1. `Microsoft.Data.Sqlite` depends on `Microsoft.Data.Sqlite.Core`
2. Other packages (like `mostlylucid.ephemeral.complete` in our case) pull in incompatible SQLite providers
3. The AOT linker gets confused about which native library to use

### The Solution: Use the Bundled SQLite

Use `SQLitePCLRaw.bundle_e_sqlite3` which ships cross-platform native SQLite binaries that work with AOT:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
  <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.10" />
</ItemGroup>
```

**Why this works:**

- `SQLitePCLRaw.bundle_e_sqlite3` includes native SQLite libraries for:
  - Windows (x64, x86, ARM64)
  - Linux (x64, ARM64, musl)
  - macOS (x64, ARM64)
- The bundle initializes automatically before your code runs
- It's AOT-safe (no dynamic loading)

### What About `winsqlite3`?

You might see recommendations to use `SQLitePCLRaw.provider.winsqlite3` on Windows to use the OS-provided SQLite. **Don't do this for cross-platform apps**. It only works on Windows and requires manual initialization code. Stick with the bundle.

### Critical: Initialize the Bundle Early

With Native AOT, the bundle doesn't always auto-initialize. You **must** call `SQLitePCL.Batteries.Init()` at the very start of your application:

```csharp
using SQLitePCL;

// FIRST LINE - before any other code
SQLitePCL.Batteries.Init();

// Now safe to use SQLite
var builder = WebApplication.CreateBuilder(args);
// ... rest of your app
```

**Put this before:**
- Any dependency injection setup
- Any database connections
- Any service configuration

Without this call, you'll get runtime errors like:
```
DllNotFoundException: Unable to load DLL 'e_sqlite3' or one of its dependencies
```

Even though the DLL is bundled correctly, AOT doesn't trigger the automatic initialization that works in normal .NET.

<a name="github-actions"></a>
## Multi-Platform GitHub Actions Configuration

Here's the complete GitHub Actions workflow for building all platforms:

```yaml
name: Build Native AOT Binaries

on:
  push:
    tags:
      - 'v*'
  workflow_dispatch:

permissions:
  contents: write

jobs:
  build-binaries:
    name: Build ${{ matrix.runtime }}
    runs-on: ${{ matrix.os }}
    strategy:
      matrix:
        include:
          # Linux x64
          - os: ubuntu-latest
            runtime: linux-x64
            artifact-name: myapp-linux-x64
            file-ext: ''

          # Linux ARM64 (Raspberry Pi 4/5)
          - os: ubuntu-latest
            runtime: linux-arm64
            artifact-name: myapp-linux-arm64
            file-ext: ''

          # Windows x64
          - os: windows-latest
            runtime: win-x64
            artifact-name: myapp-win-x64
            file-ext: '.exe'

          # macOS x64 (Intel)
          - os: macos-latest
            runtime: osx-x64
            artifact-name: myapp-osx-x64
            file-ext: ''

          # macOS ARM64 (Apple Silicon)
          - os: macos-latest
            runtime: osx-arm64
            artifact-name: myapp-osx-arm64
            file-ext: ''

    steps:
    - name: Checkout code
      uses: actions/checkout@v4

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '9.0.x'

    - name: Install ARM64 cross-compilation tools (Linux ARM64 only)
      if: matrix.runtime == 'linux-arm64'
      run: |
        sudo apt-get update
        sudo apt-get install -y clang zlib1g-dev gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu

    - name: Restore dependencies
      run: dotnet restore

    - name: Publish ${{ matrix.runtime }} binary
      shell: bash
      run: |
        # Set objcopy for ARM64 cross-compilation
        if [ "${{ matrix.runtime }}" = "linux-arm64" ]; then
          OBJCOPY_PARAM="-p:ObjCopyName=aarch64-linux-gnu-objcopy"
        else
          OBJCOPY_PARAM=""
        fi

        dotnet publish \
          --configuration Release \
          --runtime ${{ matrix.runtime }} \
          --self-contained true \
          --output ./publish/${{ matrix.runtime }} \
          -p:PublishAot=true \
          -p:PublishTrimmed=true \
          -p:TrimMode=full \
          -p:PublishSingleFile=true \
          -p:StripSymbols=true \
          -p:Version=${{ github.ref_name }} \
          $OBJCOPY_PARAM

    - name: Create distribution package
      shell: bash
      run: |
        mkdir -p ./dist
        cd ./publish/${{ matrix.runtime }}

        # Copy binary
        cp myapp${{ matrix.file-ext }} ../../dist/

        # Copy config files (if needed)
        cp ../../appsettings.json ../../dist/ || true

        cd ../../dist

        # Create archive
        if [ "${{ runner.os }}" = "Windows" ]; then
          7z a -tzip ../${{ matrix.artifact-name }}.zip *
        else
          tar czf ../${{ matrix.artifact-name }}.tar.gz *
        fi

    - name: Upload artifact
      uses: actions/upload-artifact@v4
      with:
        name: ${{ matrix.artifact-name }}
        path: |
          ${{ matrix.artifact-name }}.zip
          ${{ matrix.artifact-name }}.tar.gz
        retention-days: 7
        if-no-files-found: ignore

  create-release:
    name: Create GitHub Release
    needs: build-binaries
    runs-on: ubuntu-latest

    steps:
    - name: Download all artifacts
      uses: actions/download-artifact@v4
      with:
        path: artifacts

    - name: Prepare release assets
      run: |
        mkdir -p release-assets
        find artifacts -type f \( -name "*.zip" -o -name "*.tar.gz" \) -exec cp {} release-assets/ \;
        ls -lh release-assets/

    - name: Generate checksums
      working-directory: release-assets
      run: |
        sha256sum * > SHA256SUMS.txt
        cat SHA256SUMS.txt

    - name: Create GitHub Release
      uses: softprops/action-gh-release@v1
      with:
        name: Release ${{ github.ref_name }}
        draft: false
        files: release-assets/*
        fail_on_unmatched_files: true
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

### Key Points Explained

#### 1. Matrix Strategy

The `matrix` defines all platform builds that run **in parallel**. Each gets its own runner:

- `ubuntu-latest` for Linux builds (x64 and ARM64)
- `windows-latest` for Windows
- `macos-latest` for macOS (both Intel and Apple Silicon)

#### 2. ARM64 Cross-Compilation

Linux ARM64 builds require cross-compilation tools:

```yaml
- name: Install ARM64 cross-compilation tools (Linux ARM64 only)
  if: matrix.runtime == 'linux-arm64'
  run: |
    sudo apt-get update
    sudo apt-get install -y clang zlib1g-dev gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu
```

And you must specify the `objcopy` tool:

```bash
-p:ObjCopyName=aarch64-linux-gnu-objcopy
```

Without this, the linker fails with cryptic errors about unrecognized file formats.

#### 3. Publishing Parameters

All the important flags from the `.csproj` can be overridden at publish time:

```bash
dotnet publish \
  -p:PublishAot=true \
  -p:PublishTrimmed=true \
  -p:TrimMode=full \
  -p:PublishSingleFile=true \
  -p:StripSymbols=true
```

This lets you have different settings for debug vs release, or platform-specific tweaks.

#### 4. Artifact Upload

Each platform build uploads its archive as a separate artifact. The `create-release` job downloads them all and attaches them to a GitHub Release.

<a name="pitfalls"></a>
## Common Pitfalls and Solutions

### 1. "Cannot find vswhere.exe" (Windows)

**Problem**: On Windows, the AOT linker needs Visual Studio's MSVC linker. If you build outside a VS Developer Command Prompt, you'll get:

```
'vswhere.exe' is not recognized as an internal or external command
```

**Solution**: Run builds from **Developer Command Prompt for VS** or **Developer PowerShell for VS**. GitHub Actions handles this automatically with `windows-latest`.

If building locally, create a helper script:

```powershell
# build-aot.ps1
$ErrorActionPreference = "Stop"

# Add VS Installer to PATH
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"

# Initialize VS Developer environment
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64

# Build
dotnet publish -c Release -r win-x64 --self-contained
```

### 2. Trim Warnings (IL2026, IL3050)

**Problem**: You'll see warnings like:

```
warning IL2026: Using member 'System.Text.Json.JsonSerializer.Deserialize<T>(string)'
which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming
```

**What this means**: The trimmer can't statically analyze which types `JsonSerializer.Deserialize<T>` needs, so it might remove required code.

**Solutions**:

#### Option 1: Use Source Generators (Best)

```csharp
[JsonSerializable(typeof(MyType))]
[JsonSerializable(typeof(List<MyOtherType>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

// Usage
var obj = JsonSerializer.Deserialize<MyType>(json, AppJsonContext.Default.MyType);
```

#### Option 2: Dynamic Dependency Attributes

```csharp
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public class MyType
{
    public string Name { get; set; }
}
```

#### Option 3: Suppress Warnings (Use Sparingly)

If you've tested and know it works:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);IL2026;IL3050</NoWarn>
</PropertyGroup>
```

**Only suppress warnings after thorough testing.** These warnings exist for a reason.

### 3. "LINK: fatal error LNK1104: cannot open file"

**Problem**: The linker can't write the output file, usually because:
1. A previous build is still running
2. The executable is locked by antivirus
3. File permissions issue

**Solution**:
```bash
# Kill any running processes
taskkill /F /IM myapp.exe 2>nul

# Clean and rebuild
dotnet clean -c Release
dotnet publish -c Release -r win-x64
```

### 4. Missing Native Dependencies at Runtime

**Problem**: Your app builds fine but crashes at runtime with:

```
DllNotFoundException: Unable to load DLL 'mylibrary'
```

**Common causes**:
- SQLite (covered above—use the bundle)
- Other native libraries (OpenSSL, libcurl, etc.)

**Solution**: Ensure native dependencies are either:
1. Bundled in your NuGet package (like `SQLitePCLRaw.bundle_e_sqlite3`)
2. Installed on the target system (document this requirement)
3. Deployed alongside your binary

For custom native libraries, use `<NativeLibrary>` in your `.csproj`:

```xml
<ItemGroup>
  <NativeLibrary Include="path/to/mylibrary.so" />
</ItemGroup>
```

### 5. Larger-than-Expected Binaries

**Problem**: Your "tiny" executable is 50MB+.

**Diagnosis**:
```bash
# Analyze what's taking up space
dotnet publish -c Release -r linux-x64 -p:PublishAot=true /p:LinkerDumpDependencies=true
```

**Common culprits**:
- `InvariantGlobalization=false` (adds 10-15MB of culture data)
- Unused large dependencies (Blazor, SignalR, gRPC if you don't need them)
- Debug symbols not stripped (`StripSymbols=false`)

**Solutions**:
1. Set `InvariantGlobalization=true` if possible
2. Remove unused package references
3. Enable `StripSymbols=true`
4. Use `OptimizationPreference=Size` instead of `Speed`

<a name="results"></a>
## Real-World Results

Here's what you can expect with a typical ASP.NET Core application (YARP reverse proxy + middleware + SQLite):

### Build Output

| Platform | Binary Size | Native Dependencies | Total |
|----------|-------------|---------------------|-------|
| Windows x64 | 27MB | 1.7MB (e_sqlite3.dll) | ~29MB |
| Linux x64 | 25MB | 1.6MB (libe_sqlite3.so) | ~27MB |
| Linux ARM64 | 24MB | 1.5MB (libe_sqlite3.so) | ~26MB |
| macOS x64 | 28MB | 1.8MB (libe_sqlite3.dylib) | ~30MB |
| macOS ARM64 | 26MB | 1.7MB (libe_sqlite3.dylib) | ~28MB |

Compare this to self-contained .NET 9 deployments:
- **130-150MB** per platform
- Requires extracting runtime on first launch
- Slower startup

### Startup Time

Measured cold start (first request served):

| Deployment Type | Startup Time |
|----------------|--------------|
| Self-contained .NET | ~800ms |
| Native AOT | ~150ms |
| **Improvement** | **~80% faster** |

### Memory Usage

Idle memory (gateway running, no traffic):

| Deployment Type | Memory (MB) |
|----------------|-------------|
| Self-contained .NET | 85MB |
| Native AOT | 42MB |
| **Improvement** | **~50% less** |

## Conclusion

Native AOT opens up new possibilities for .NET applications—tiny binaries, instant startup, zero runtime dependencies. The setup requires careful attention to reflection, JSON serialization, and native dependencies (especially SQLite), but the payoff is substantial.

For CLI tools, gateways, containers, and edge devices, AOT is the clear winner. For applications heavy on reflection, EF Core, or dynamic features, stick with traditional .NET deployments or be prepared for significant testing and attribute annotations.

The GitHub Actions workflow provided here builds all major platforms automatically—just tag a release and you're done. Welcome to the world of truly portable .NET applications.

## Resources

- [Official .NET Native AOT Docs](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [SQLitePCLRaw Documentation](https://github.com/ericsink/SQLitePCL.raw)
- [System.Text.Json Source Generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [Trimming Warning Codes](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/fixing-warnings)

## Complete Example Repository

For a complete working example with GitHub Actions, see:
[Mostlylucid Bot Detection Console Gateway](https://github.com/scottgal/mostlylucid.nugetpackages/tree/main/Mostlylucid.BotDetection.Console)

---

*Published December 2024 - Updated for .NET 9/10*

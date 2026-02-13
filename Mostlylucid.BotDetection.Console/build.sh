#!/bin/bash
# Build script for Mostlylucid.BotDetection.Console
# Builds AOT-compiled single-file executables for multiple platforms

set -e

TARGET="${1:-all}"
CONFIGURATION="${2:-Release}"

echo "╔══════════════════════════════════════════════════════════╗"
echo "║   Mostlylucid Bot Detection Console - Build Script      ║"
echo "╚══════════════════════════════════════════════════════════╝"
echo ""

PROJECT_PATH="Mostlylucid.BotDetection.Console.csproj"

build_target() {
    local RUNTIME_ID=$1
    local PLATFORM_NAME=$2

    echo "Building for $PLATFORM_NAME ($RUNTIME_ID)..."

    OUTPUT_PATH="bin/$CONFIGURATION/net9.0/$RUNTIME_ID/publish"

    # Clean previous build
    if [ -d "$OUTPUT_PATH" ]; then
        rm -rf "$OUTPUT_PATH"
    fi

    # Build
    dotnet publish "$PROJECT_PATH" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME_ID" \
        --self-contained \
        /p:PublishAot=true \
        /p:PublishTrimmed=true \
        /p:PublishSingleFile=true \
        /p:TrimMode=full \
        /p:StripSymbols=true \
        /p:OptimizationPreference=Speed \
        /p:IlcOptimizationPreference=Speed

    # Get file size
    if [[ "$RUNTIME_ID" == win-* ]]; then
        EXE_NAME="minigw.exe"
    else
        EXE_NAME="minigw"
    fi

    EXE_PATH="$OUTPUT_PATH/$EXE_NAME"

    if [ -f "$EXE_PATH" ]; then
        SIZE=$(du -h "$EXE_PATH" | cut -f1)
        echo "✓ Built successfully: $EXE_PATH ($SIZE)"
    fi

    echo ""
}

# Build targets
case "$TARGET" in
    all)
        echo "Building all targets..."
        echo ""
        build_target "win-x64" "Windows x64"
        build_target "win-arm64" "Windows ARM64 (Surface Pro X)"
        build_target "linux-x64" "Linux x64"
        build_target "linux-arm64" "Linux ARM64 (Raspberry Pi)"
        build_target "osx-x64" "macOS x64 (Intel)"
        build_target "osx-arm64" "macOS ARM64 (Apple Silicon)"
        ;;
    win-x64)
        build_target "win-x64" "Windows x64"
        ;;
    win-arm64)
        build_target "win-arm64" "Windows ARM64 (Surface Pro X)"
        ;;
    linux-x64)
        build_target "linux-x64" "Linux x64"
        ;;
    linux-arm64)
        build_target "linux-arm64" "Linux ARM64 (Raspberry Pi)"
        ;;
    osx-x64)
        build_target "osx-x64" "macOS x64 (Intel)"
        ;;
    osx-arm64)
        build_target "osx-arm64" "macOS ARM64 (Apple Silicon)"
        ;;
    *)
        echo "Unknown target: $TARGET"
        echo "Valid targets: all, win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64"
        exit 1
        ;;
esac

echo "═══════════════════════════════════════════════════════════"
echo "Build complete!"
echo "═══════════════════════════════════════════════════════════"
echo ""
echo "Executables are in bin/$CONFIGURATION/net9.0/{runtime}/publish/"
echo ""
echo "Example usage:"
echo "  Linux/macOS:  ./minigw --upstream http://backend:8080 --port 5080"
echo "  Windows:      ./minigw.exe --upstream http://backend:8080 --port 5080"
echo ""

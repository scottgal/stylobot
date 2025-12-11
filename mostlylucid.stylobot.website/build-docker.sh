#!/bin/bash
# Bash script to build Docker image for Stylobot Website

set -e

# Default values
TAG="latest"
IMAGE_NAME="stylobot-website"
NO_CACHE=false
SAVE_TARBALL=false
COMPRESS=false

# Color codes
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -t|--tag)
            TAG="$2"
            shift 2
            ;;
        -n|--name)
            IMAGE_NAME="$2"
            shift 2
            ;;
        --no-cache)
            NO_CACHE=true
            shift
            ;;
        -s|--save)
            SAVE_TARBALL=true
            shift
            ;;
        -c|--compress)
            COMPRESS=true
            shift
            ;;
        -h|--help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  -t, --tag TAG         Image tag (default: latest)"
            echo "  -n, --name NAME       Image name (default: stylobot-website)"
            echo "  --no-cache            Build without cache"
            echo "  -s, --save            Save image as tarball"
            echo "  -c, --compress        Save and compress image as .tar.gz"
            echo "  -h, --help            Show this help message"
            echo ""
            echo "Examples:"
            echo "  $0                           # Build with defaults"
            echo "  $0 -t v1.0.0                 # Build with specific tag"
            echo "  $0 -c                        # Build and create compressed tarball"
            echo "  $0 -t v1.0.0 -c              # Build specific version and compress"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            echo "Use -h or --help for usage information"
            exit 1
            ;;
    esac
done

echo -e "${CYAN}========================================${NC}"
echo -e "${CYAN}Building Stylobot Website Docker Image${NC}"
echo -e "${CYAN}========================================${NC}"
echo ""

# Get script directory (project root)
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
echo -e "${YELLOW}Project Root: ${PROJECT_ROOT}${NC}"

# Full image tag
FULL_TAG="${IMAGE_NAME}:${TAG}"
echo -e "${GREEN}Building: ${FULL_TAG}${NC}"
echo ""

# Build arguments
BUILD_ARGS="build -t ${FULL_TAG} -f Dockerfile ."

if [ "$NO_CACHE" = true ]; then
    echo -e "${YELLOW}Building with --no-cache flag${NC}"
    BUILD_ARGS="${BUILD_ARGS} --no-cache"
fi

# Build the Docker image
echo -e "${CYAN}Running: docker ${BUILD_ARGS}${NC}"
cd "$PROJECT_ROOT"

if docker $BUILD_ARGS; then
    echo ""
    echo -e "${GREEN}========================================${NC}"
    echo -e "${GREEN}Build Successful!${NC}"
    echo -e "${GREEN}========================================${NC}"
    echo ""
    echo -e "${CYAN}Image: ${FULL_TAG}${NC}"

    # Get image info
    IMAGE_SIZE=$(docker images ${FULL_TAG} --format "{{.Size}}")
    echo -e "${CYAN}Size: ${IMAGE_SIZE}${NC}"
    echo ""

    # Save as tarball if requested
    if [ "$SAVE_TARBALL" = true ] || [ "$COMPRESS" = true ]; then
        OUTPUT_DIR="${PROJECT_ROOT}/dist"
        mkdir -p "$OUTPUT_DIR"

        TAR_FILE="${OUTPUT_DIR}/${IMAGE_NAME}-${TAG}.tar"

        echo -e "${CYAN}========================================${NC}"
        echo -e "${CYAN}Saving Docker Image as Tarball${NC}"
        echo -e "${CYAN}========================================${NC}"
        echo ""
        echo -e "${YELLOW}Saving to: ${TAR_FILE}${NC}"

        if docker save -o "$TAR_FILE" "$FULL_TAG"; then
            TAR_SIZE=$(du -h "$TAR_FILE" | cut -f1)
            echo -e "${GREEN}Tarball created: ${TAR_SIZE}${NC}"

            # Compress if requested
            if [ "$COMPRESS" = true ]; then
                echo ""
                echo -e "${CYAN}Compressing tarball with gzip...${NC}"

                GZ_FILE="${TAR_FILE}.gz"

                if gzip -f "$TAR_FILE"; then
                    GZ_SIZE=$(du -h "$GZ_FILE" | cut -f1)
                    echo -e "${GREEN}Compressed: ${GZ_SIZE}${NC}"
                    echo ""
                    echo -e "${GREEN}========================================${NC}"
                    echo -e "${GREEN}Compressed Image Ready for Deployment!${NC}"
                    echo -e "${GREEN}========================================${NC}"
                    echo ""
                    echo -e "${CYAN}File: ${GZ_FILE}${NC}"
                    echo ""
                    echo -e "${YELLOW}To deploy on remote server:${NC}"
                    echo -e "  ${NC}1. Copy to server: scp ${GZ_FILE} user@server:/tmp/${NC}"
                    echo -e "  ${NC}2. On server: gunzip -c /tmp/$(basename ${GZ_FILE}) | docker load${NC}"
                    echo -e "  ${NC}3. Run: docker-compose up -d${NC}"
                else
                    echo -e "${RED}Compression failed!${NC}"
                    exit 1
                fi
            else
                echo ""
                echo -e "${YELLOW}To load on another machine:${NC}"
                echo -e "  ${NC}docker load -i ${TAR_FILE}${NC}"
            fi
        else
            echo -e "${RED}Docker save failed!${NC}"
            exit 1
        fi
    fi

    echo ""
    echo -e "${YELLOW}To run locally:${NC}"
    echo -e "  ${NC}docker run -d -p 8080:8080 --name stylobot ${FULL_TAG}${NC}"
    echo ""
    echo -e "${YELLOW}To run with docker-compose:${NC}"
    echo -e "  ${NC}docker-compose up -d${NC}"
    echo ""

else
    echo -e "${RED}Docker build failed!${NC}"
    exit 1
fi

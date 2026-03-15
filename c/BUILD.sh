#!/bin/bash
# Build script for Aether Protocol C Library

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
BUILD_DIR="${SCRIPT_DIR}/build"

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}=== Aether Protocol C Library Build ===${NC}"

# Check for required tools
echo -e "${BLUE}Checking dependencies...${NC}"

if ! command -v cmake &> /dev/null; then
    echo -e "${RED}ERROR: cmake not found. Install with: brew install cmake (macOS) or apt-get install cmake (Linux)${NC}"
    exit 1
fi

if ! pkg-config --exists libsodium; then
    echo -e "${RED}ERROR: libsodium not found. Install with: brew install libsodium (macOS) or apt-get install libsodium-dev (Linux)${NC}"
    exit 1
fi

echo -e "${GREEN}✓ CMake found${NC}"
echo -e "${GREEN}✓ libsodium found${NC}"

# Create build directory
mkdir -p "${BUILD_DIR}"
cd "${BUILD_DIR}"

# Run CMake
echo -e "${BLUE}Configuring with CMake...${NC}"
cmake ..

# Build
echo -e "${BLUE}Building...${NC}"
make

# Run tests if available
if [ -f "ctest" ] || command -v ctest &> /dev/null; then
    echo -e "${BLUE}Running tests...${NC}"
    ctest --output-on-failure --verbose || true
fi

echo -e "${GREEN}=== Build complete ===${NC}"
echo -e "${BLUE}Demo executable: ${BUILD_DIR}/aether-demo${NC}"
echo -e "${BLUE}Library: ${BUILD_DIR}/libaether-protocol.a${NC}"
echo -e "${BLUE}Run demo with: ${BUILD_DIR}/aether-demo${NC}"

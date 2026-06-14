cmake_minimum_required(VERSION 3.16)
project(aethernet-protocol C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

# Find libsodium
find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBSODIUM REQUIRED libsodium)

# ─── cJSON (required for voice/streaming JSON signalling) ──────────────────
# Fetched at configure time so the build is self-contained on Linux CI hosts.
include(FetchContent)
FetchContent_Declare(
    cjson
    GIT_REPOSITORY https://github.com/DaveGamble/cJSON.git
    GIT_TAG        v1.7.18
    GIT_SHALLOW    TRUE
)
set(CJSON_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(ENABLE_CJSON_TEST OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(cjson)

# ─── BLAKE3 (portable C reference, no SIMD) ────────────────────────────────
# We fetch the upstream BLAKE3 repo and compile only the portable three-file
# subset directly into aethernet-protocol. This avoids SIMD toolchain issues on
# cross-compile CI targets while remaining wire-compatible with every other
# language implementation in this family.
FetchContent_Declare(
    blake3_src
    GIT_REPOSITORY https://github.com/BLAKE3-team/BLAKE3.git
    GIT_TAG        1.5.1
    GIT_SHALLOW    TRUE
)
FetchContent_GetProperties(blake3_src)
if(NOT blake3_src_POPULATED)
    FetchContent_Populate(blake3_src)
endif()

# cJSON's FetchContent layout puts cJSON.h directly in ${cjson_SOURCE_DIR}
# (i.e. #include <cJSON.h>), but our source files use the system-package
# convention #include <cjson/cJSON.h>.  Create a thin shim directory so both
# paths resolve to the same header regardless of whether we're using
# FetchContent or a system-installed libcjson-dev package.
set(CJSON_SHIM_DIR "${CMAKE_CURRENT_BINARY_DIR}/cjson-shim")
file(MAKE_DIRECTORY "${CJSON_SHIM_DIR}/cjson")
configure_file("${cjson_SOURCE_DIR}/cJSON.h"
               "${CJSON_SHIM_DIR}/cjson/cJSON.h" COPYONLY)

# Add the library
add_library(aethernet-protocol STATIC
    src/protocol.c
    src/security.c
    src/signal_protocol.c
    src/transport_inprocess.c
    src/transport_metrics.c
    src/predictive_selector.c
    src/rlnc.c
    src/routing.c
    src/dtn.c
    src/sos.c
    src/voice.c
    src/group_voice.c
    src/streaming.c
    src/aethernet_reputation.c
    src/aethernet_anomaly.c
    src/aethernet_gossip.c
    src/aethernet_tag.c
    src/erid.c
    src/aethernet_uri.c
    src/aethernet_bandwidth.c
    src/chunk_bitmap.c
    src/directory_service.c
    src/tip_packet.c
    src/reed_solomon.c
    src/pov_token.c
    # BLAKE3 portable subset (no SIMD — compile-time defines below disable all
    # optional SIMD backends; only blake3_portable.c is linked in practice)
    ${blake3_src_SOURCE_DIR}/c/blake3.c
    ${blake3_src_SOURCE_DIR}/c/blake3_dispatch.c
    ${blake3_src_SOURCE_DIR}/c/blake3_portable.c
)

target_include_directories(aethernet-protocol
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${LIBSODIUM_INCLUDE_DIRS}
        ${cjson_SOURCE_DIR}
        ${CJSON_SHIM_DIR}
        ${blake3_src_SOURCE_DIR}/c
)

# Disable all SIMD backends — only the portable C path is compiled.
# Each define removes one optional .c file from the dispatch chain;
# without them blake3_dispatch.c would try to compile SSE2/AVX2/NEON
# assembly stubs which may not exist on all CI toolchains.
target_compile_definitions(aethernet-protocol PRIVATE
    BLAKE3_NO_SSE2=1
    BLAKE3_NO_SSE41=1
    BLAKE3_NO_AVX2=1
    BLAKE3_NO_AVX512=1
    # Upstream BLAKE3 uses BLAKE3_USE_NEON (0|1), NOT BLAKE3_NO_NEON,
    # to gate ARM NEON. On Apple Silicon the autodetect defaults to 1
    # and the dispatch tries to link blake3_hash_many_neon - which we
    # never compile here. Force the portable path.
    BLAKE3_USE_NEON=0
)

target_link_directories(aethernet-protocol
    PUBLIC
        ${LIBSODIUM_LIBRARY_DIRS}
)

target_link_libraries(aethernet-protocol
    PUBLIC
        pthread
    PRIVATE
        ${LIBSODIUM_LIBRARIES}
        cjson
)

# Add the demo executable
add_executable(aethernet-demo
    src/demo.c
)

target_include_directories(aethernet-demo
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_include_directories(aethernet-demo
    PRIVATE
        ${LIBSODIUM_INCLUDE_DIRS}
)

target_link_directories(aethernet-demo
    PRIVATE
        ${LIBSODIUM_LIBRARY_DIRS}
)

target_link_libraries(aethernet-demo
    PRIVATE
        aethernet-protocol
        ${LIBSODIUM_LIBRARIES}
        pthread
)

# Optional: Add tests directory
enable_testing()
add_subdirectory(tests EXCLUDE_FROM_ALL)

# Optional bench harness — separate target, not in ctest. Run via
#   cmake --build . --target bench && ./bench/bench
# Mirrors the cases pinned by go/bench (commit f873543) and
# python/benchmarks/ so a regression in any language shows up as a delta
# against the saved baseline.
add_subdirectory(bench EXCLUDE_FROM_ALL)

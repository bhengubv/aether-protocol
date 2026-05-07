cmake_minimum_required(VERSION 3.16)
project(aether-protocol C)

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

# Add the library
add_library(aether-protocol STATIC
    src/protocol.c
    src/security.c
    src/transport_inprocess.c
    src/routing.c
    src/dtn.c
    src/sos.c
    src/voice.c
    src/group_voice.c
    src/streaming.c
)

target_include_directories(aether-protocol
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${LIBSODIUM_INCLUDE_DIRS}
)

target_link_directories(aether-protocol
    PUBLIC
        ${LIBSODIUM_LIBRARY_DIRS}
)

target_link_libraries(aether-protocol
    PUBLIC
        pthread
    PRIVATE
        ${LIBSODIUM_LIBRARIES}
        cjson
)

# Add the demo executable
add_executable(aether-demo
    src/demo.c
)

target_include_directories(aether-demo
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_include_directories(aether-demo
    PRIVATE
        ${LIBSODIUM_INCLUDE_DIRS}
)

target_link_directories(aether-demo
    PRIVATE
        ${LIBSODIUM_LIBRARY_DIRS}
)

target_link_libraries(aether-demo
    PRIVATE
        aether-protocol
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

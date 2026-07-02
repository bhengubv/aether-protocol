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

# ─── WebRTC P2P transport (libdatachannel) ─────────────────────────────────
# Optional, OFF by default: the real RTCDataChannel transport pulls a sizeable
# C++ dependency tree (libdatachannel + libjuice + usrsctp + MbedTLS) that not
# every consumer of this library needs. Enable with -DAETHERNET_WITH_WEBRTC=ON.
#
# libdatachannel needs a DTLS backend. Rather than depend on a system OpenSSL,
# we fetch MbedTLS (Apache-2.0, pure C, builds cleanly with MinGW/GCC) and point
# libdatachannel at it via USE_MBEDTLS — keeping the build self-contained, the
# same FetchContent-at-configure-time pattern used for cJSON and BLAKE3 above.
option(AETHERNET_WITH_WEBRTC "Build the real WebRTC P2P transport (libdatachannel)" OFF)

if(AETHERNET_WITH_WEBRTC)
    # libdatachannel's install(EXPORT LibDataChannelTargets) pulls in the `datachannel`
    # target, which links our MbedTLS::MbedTLS aggregate INTERFACE target — and that
    # aggregate isn't in any export set, so the generate step errors. We static-link the
    # data-channel library into aethernet-protocol and never `make install` it, so disable
    # install rules entirely for the WebRTC-enabled build.
    set(CMAKE_SKIP_INSTALL_RULES ON)

    # libdatachannel's bundled libjuice uses POSIX socket APIs (getaddrinfo, NI_*/AI_*,
    # struct addrinfo) that glibc hides under a strict -std=c99 unless a feature-test macro
    # is defined. Expose them across the whole WebRTC dependency tree on Linux; this is a
    # no-op on macOS/BSD, which expose those symbols by default.
    add_compile_definitions(_GNU_SOURCE)

    enable_language(CXX)   # libdatachannel is C++ internally (C API surface).
    set(CMAKE_CXX_STANDARD 17)
    set(CMAKE_CXX_STANDARD_REQUIRED ON)

    # MbedTLS 3.x — DTLS backend for libdatachannel.
    FetchContent_Declare(
        mbedtls
        GIT_REPOSITORY https://github.com/Mbed-TLS/mbedtls.git
        GIT_TAG        v3.6.2
        GIT_SHALLOW    TRUE
    )
    set(ENABLE_TESTING   OFF CACHE BOOL "" FORCE)
    set(ENABLE_PROGRAMS  OFF CACHE BOOL "" FORCE)
    set(MBEDTLS_FATAL_WARNINGS OFF CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(mbedtls)

    # libdatachannel's DTLS transport calls MbedTLS's DTLS-SRTP API
    # (mbedtls_ssl_conf_dtls_srtp_protection_profiles), which MbedTLS only compiles when
    # MBEDTLS_SSL_DTLS_SRTP is defined — the stock config leaves it off. Enable it on the
    # MbedTLS targets (PUBLIC so libdatachannel is compiled against the same definition).
    foreach(_mbed mbedtls mbedx509 mbedcrypto)
        if(TARGET ${_mbed})
            target_compile_definitions(${_mbed} PUBLIC MBEDTLS_SSL_DTLS_SRTP)
        endif()
    endforeach()

    # libdatachannel's find logic wants a single aggregate MbedTLS::MbedTLS
    # target (it checks `if(NOT TARGET MbedTLS::MbedTLS)`), but MbedTLS exports
    # the three component targets. Provide the aggregate so find_package is skipped.
    if(NOT TARGET MbedTLS::MbedTLS)
        add_library(MbedTLS_aggregate INTERFACE)
        target_link_libraries(MbedTLS_aggregate INTERFACE
            MbedTLS::mbedtls MbedTLS::mbedx509 MbedTLS::mbedcrypto)
        add_library(MbedTLS::MbedTLS ALIAS MbedTLS_aggregate)
    endif()

    # libdatachannel — data-channel-only build (no media/SRTP, no WebSocket) to
    # cut the dependency surface to libjuice + usrsctp + plog + MbedTLS.
    FetchContent_Declare(
        libdatachannel
        GIT_REPOSITORY https://github.com/paullouisageneau/libdatachannel.git
        GIT_TAG        v0.22.4
        GIT_SHALLOW    TRUE
        GIT_SUBMODULES_RECURSE TRUE
    )
    set(USE_MBEDTLS     ON  CACHE BOOL "" FORCE)
    set(USE_GNUTLS      OFF CACHE BOOL "" FORCE)
    set(USE_NICE        OFF CACHE BOOL "" FORCE)
    set(NO_MEDIA        ON  CACHE BOOL "" FORCE)
    set(NO_WEBSOCKET    ON  CACHE BOOL "" FORCE)
    set(NO_EXAMPLES     ON  CACHE BOOL "" FORCE)
    set(NO_TESTS        ON  CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(libdatachannel)
endif()

# Add the library
# ----- micro-ecc (NIST P-256 / secp256r1 ECDSA verify) ---------------------
# libsodium provides no NIST P-256; the Ed25519->P-256 migration verify fallback
# (PROTOCOL_SPEC.md 7.5) needs raw secp256r1 ECDSA. micro-ecc is a single
# portable C source file (BSD-2) compiled directly into the library the same
# FetchContent-populate way as BLAKE3 above, so the default build stays
# self-contained and free of the heavier MbedTLS stack used only by the
# optional WebRTC path.
FetchContent_Declare(
    micro_ecc
    GIT_REPOSITORY https://github.com/kmackay/micro-ecc.git
    GIT_TAG        v1.1
    GIT_SHALLOW    TRUE
)
FetchContent_GetProperties(micro_ecc)
if(NOT micro_ecc_POPULATED)
    FetchContent_Populate(micro_ecc)
endif()

add_library(aethernet-protocol STATIC
    src/protocol.c
    src/security.c
    src/signal_protocol.c
    src/transport_inprocess.c
    src/transport_lora.c
    src/transport_metrics.c
    src/predictive_selector.c
    src/rlnc.c
    src/routing.c
    src/dtn.c
    src/dtn_envelope.c
    src/circuit_relay.c
    src/relay_transport.c
    src/relay_mesh_link.c
    src/forge.c
    src/fmhy.c
    src/sos.c
    src/channels.c
    src/profiles.c
    src/heartbeat.c
    src/space.c
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
    src/peer_id.c
    src/tip_packet.c
    src/reed_solomon.c
    src/vault.c
    src/pov_token.c
    src/market.c
    # BLAKE3 portable subset (no SIMD — compile-time defines below disable all
    # optional SIMD backends; only blake3_portable.c is linked in practice)
    ${blake3_src_SOURCE_DIR}/c/blake3.c
    ${blake3_src_SOURCE_DIR}/c/blake3_dispatch.c
    ${blake3_src_SOURCE_DIR}/c/blake3_portable.c
    # micro-ecc P-256 (secp256r1) ECDSA - single portable source file.
    ${micro_ecc_SOURCE_DIR}/uECC.c
)

# WebRTC transport sources compile only when the dependency is built — they
# #include <rtc/rtc.h>, which only exists once libdatachannel is fetched.
if(AETHERNET_WITH_WEBRTC)
    target_sources(aethernet-protocol PRIVATE
        src/transport_webrtc.c
        src/transport_webrtc_signaling.c
    )
endif()

target_include_directories(aethernet-protocol
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${LIBSODIUM_INCLUDE_DIRS}
        ${cjson_SOURCE_DIR}
        ${CJSON_SHIM_DIR}
        ${micro_ecc_SOURCE_DIR}
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

# Link libdatachannel (static) for the WebRTC transport. PUBLIC so that test
# executables linking aethernet-protocol pull in the data-channel symbols too.
if(AETHERNET_WITH_WEBRTC)
    target_link_libraries(aethernet-protocol PUBLIC datachannel-static)
    target_compile_definitions(aethernet-protocol PUBLIC AETHERNET_WITH_WEBRTC=1)
endif()

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

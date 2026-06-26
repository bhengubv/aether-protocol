# Tests for Aether Protocol

add_executable(test-protocol
    test_protocol.c
)
target_include_directories(test-protocol PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-protocol PRIVATE aethernet-protocol pthread)
add_test(NAME ProtocolTests COMMAND test-protocol)

add_executable(test-routing
    test_routing.c
)
target_include_directories(test-routing PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-routing PRIVATE aethernet-protocol pthread)
add_test(NAME RoutingTests COMMAND test-routing)

add_executable(test-dtn
    test_dtn.c
)
target_include_directories(test-dtn PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-dtn PRIVATE aethernet-protocol pthread)
add_test(NAME DtnTests COMMAND test-dtn)

add_executable(test-dtn-fixture
    test_dtn_fixture.c
)
target_include_directories(test-dtn-fixture PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-dtn-fixture PRIVATE aethernet-protocol pthread)
# Run from the repo root so the relative fixtures/dtn/expected/*.bin paths resolve.
add_test(NAME DtnFixtureTests COMMAND test-dtn-fixture
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-sos
    test_sos.c
)
target_include_directories(test-sos PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-sos PRIVATE aethernet-protocol pthread)
add_test(NAME SosTests COMMAND test-sos)

add_executable(test-fixtures
    test_fixtures.c
)
target_include_directories(test-fixtures PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-fixtures PRIVATE aethernet-protocol pthread)
# Run from the repo root so the fixture-locator finds fixtures/.
add_test(NAME FixtureTests COMMAND test-fixtures
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-signal-fixtures
    test_signal_fixtures.c
)
target_include_directories(test-signal-fixtures PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-signal-fixtures PRIVATE aethernet-protocol pthread)
# Run from the repo root so find_repo_root() locates AetherNetProtocol.slnx.
add_test(NAME SignalFixtureTests COMMAND test-signal-fixtures
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Portable, dependency-free fuzz harness covering:
#   - aethernet_packet_deserialize (random bytes, oversize length prefix, mutated valid wire)
#   - Crypto primitives: aethernet_x25519_*, aethernet_hkdf_sha256, aethernet_hmac_sha256
# 10k smoke iterations by default; tunable via AETHERNET_FUZZ_ITERATIONS.
add_executable(test-fuzz
    test_fuzz.c
)
target_include_directories(test-fuzz PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-fuzz PRIVATE aethernet-protocol pthread)
add_test(NAME FuzzTests COMMAND test-fuzz)

add_executable(test-signal-session
    test_signal_session.c
)
target_include_directories(test-signal-session PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-signal-session PRIVATE aethernet-protocol pthread)
add_test(NAME SignalSession COMMAND test-signal-session)

add_executable(test-rlnc
    test_rlnc.c
)
target_include_directories(test-rlnc PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-rlnc PRIVATE aethernet-protocol pthread)
add_test(NAME RlncTests COMMAND test-rlnc)

add_executable(test-predictive-selector
    test_predictive_selector.c
)
target_include_directories(test-predictive-selector PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-predictive-selector PRIVATE aethernet-protocol pthread m)
add_test(NAME PredictiveSelectorTests COMMAND test-predictive-selector)

add_executable(test-voice
    test_voice.c
)
target_include_directories(test-voice PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-voice PRIVATE aethernet-protocol pthread)
add_test(NAME VoiceTests COMMAND test-voice)

add_executable(test-streaming
    test_streaming.c
)
target_include_directories(test-streaming PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-streaming PRIVATE aethernet-protocol pthread)
add_test(NAME StreamingTests COMMAND test-streaming)

add_executable(test-group-voice
    test_group_voice.c
)
target_include_directories(test-group-voice PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-group-voice PRIVATE aethernet-protocol pthread)
add_test(NAME GroupVoiceTests COMMAND test-group-voice)

add_executable(test-video-call
    test_video_call.c
)
target_include_directories(test-video-call PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-video-call PRIVATE aethernet-protocol pthread)
add_test(NAME VideoCallTests COMMAND test-video-call)

add_executable(test-watch-together
    test_watch_together.c
)
target_include_directories(test-watch-together PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-watch-together PRIVATE aethernet-protocol pthread)
add_test(NAME WatchTogetherTests COMMAND test-watch-together)

add_executable(test-security
    test_security.c
)
target_include_directories(test-security PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-security PRIVATE aethernet-protocol pthread)
add_test(NAME SecurityTests COMMAND test-security)

add_executable(test-transport-metrics
    test_transport_metrics.c
)
target_include_directories(test-transport-metrics PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-transport-metrics PRIVATE aethernet-protocol pthread m)
add_test(NAME TransportMetricsTests COMMAND test-transport-metrics)

add_executable(test-transport-inprocess
    test_transport_inprocess.c
)
target_include_directories(test-transport-inprocess PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-transport-inprocess PRIVATE aethernet-protocol pthread)
add_test(NAME TransportInprocessTests COMMAND test-transport-inprocess)

# Real WebRTC P2P transport (libdatachannel) — only built when the optional
# dependency is enabled at the top-level (-DAETHERNET_WITH_WEBRTC=ON).
if(AETHERNET_WITH_WEBRTC)
    add_executable(test-transport-webrtc
        test_transport_webrtc.c
    )
    target_include_directories(test-transport-webrtc PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
    target_link_libraries(test-transport-webrtc PRIVATE aethernet-protocol pthread)
    # ICE negotiation + DTLS handshake can take a few seconds on a loaded host.
    add_test(NAME TransportWebRtcTests COMMAND test-transport-webrtc)
    set_tests_properties(TransportWebRtcTests PROPERTIES TIMEOUT 120)
endif()

add_executable(test-reputation
    test_reputation.c
)
target_include_directories(test-reputation PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-reputation PRIVATE aethernet-protocol m)
add_test(NAME ReputationTests COMMAND test-reputation)

add_executable(test-anomaly
    test_anomaly.c
)
target_include_directories(test-anomaly PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-anomaly PRIVATE aethernet-protocol m)
add_test(NAME AnomalyTests COMMAND test-anomaly)

add_executable(test-gossip
    test_gossip.c
)
target_include_directories(test-gossip PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-gossip PRIVATE aethernet-protocol m)
add_test(NAME GossipTests COMMAND test-gossip)

add_executable(test-aether-tag
    test_aethernet_tag.c
)
target_include_directories(test-aether-tag PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-aether-tag PRIVATE aethernet-protocol pthread)
add_test(NAME AetherNetTagTests COMMAND test-aether-tag)

add_executable(test-uri
    test_uri.c
)
target_include_directories(test-uri PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-uri PRIVATE aethernet-protocol pthread)
add_test(NAME UriTests COMMAND test-uri)

add_executable(test-erid
    test_erid.c
)
target_include_directories(test-erid PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-erid PRIVATE aethernet-protocol pthread)
# Run from the repo root so find_repo_root() locates AetherNetProtocol.slnx + fixtures/.
add_test(NAME EridFixtureTests COMMAND test-erid
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-chunk-bitmap
    test_chunk_bitmap.c
)
target_include_directories(test-chunk-bitmap PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-chunk-bitmap PRIVATE aethernet-protocol)
add_test(NAME ChunkBitmapTests COMMAND test-chunk-bitmap)

add_executable(test-directory-service
    test_directory_service.c
)
target_include_directories(test-directory-service PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-directory-service PRIVATE aethernet-protocol pthread)
add_test(NAME DirectoryServiceTests COMMAND test-directory-service)

add_executable(test-bandwidth
    test_bandwidth.c
)
target_include_directories(test-bandwidth PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-bandwidth PRIVATE aethernet-protocol pthread m)
add_test(NAME BandwidthTests COMMAND test-bandwidth)

# Cross-language ABMF numeric-conformance driver. Transcribes the 22 cases from
# tests/cross-language/bandwidth-fixtures.json (no JSON parser on the test
# surface) and drives the C ABMF through them, mirroring the C# reference driver
# in tests/AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs.
add_executable(test-bandwidth-fixtures
    test_bandwidth_fixtures.c
)
target_include_directories(test-bandwidth-fixtures PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-bandwidth-fixtures PRIVATE aethernet-protocol pthread m)
add_test(NAME BandwidthFixtureTests COMMAND test-bandwidth-fixtures)

# ── Cross-language parity fixture tests (tipping / vault / PoV) ─────────────
# Each loads its fixtures/*.json from the repo root, so it runs with the working
# directory set to ../.. (mirroring FixtureTests / SignalFixtureTests / EridFixtureTests).

add_executable(test-tip-packet
    test_tip_packet.c
)
target_include_directories(test-tip-packet PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-tip-packet PRIVATE aethernet-protocol pthread)
add_test(NAME TipPacketFixtureTests COMMAND test-tip-packet
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-reed-solomon
    test_reed_solomon.c
)
target_include_directories(test-reed-solomon PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-reed-solomon PRIVATE aethernet-protocol pthread)
add_test(NAME ReedSolomonFixtureTests COMMAND test-reed-solomon
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-pov-token
    test_pov_token.c
)
target_include_directories(test-pov-token PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-pov-token PRIVATE aethernet-protocol pthread)
add_test(NAME PoVTokenFixtureTests COMMAND test-pov-token
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# aether-market in-memory services (Phase-2): marketplace listings/escrow + single-node
# PoV trust service. A self-contained behavioural test (no JSON fixture).
add_executable(test-market
    test_market.c
)
target_include_directories(test-market PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-market PRIVATE aethernet-protocol pthread m)
add_test(NAME MarketServiceTests COMMAND test-market)

# aether-forge in-memory package cache (Phase-2): cache/query/fetch/stats behaviour.
add_executable(test-forge
    test_forge.c
)
target_include_directories(test-forge PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-forge PRIVATE aethernet-protocol pthread)
add_test(NAME ForgeServiceTests COMMAND test-forge)

# aether-space in-memory breadcrumb noticeboard (Phase-2): drop/scan/delete/prune behaviour.
add_executable(test-space
    test_space.c
)
target_include_directories(test-space PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-space PRIVATE aethernet-protocol pthread)
add_test(NAME SpaceServiceTests COMMAND test-space)

# FMHY content catalogue (Phase-2): markdown parser + catalogue sync/browse/getStarred behaviour.
add_executable(test-fmhy
    test_fmhy.c
)
target_include_directories(test-fmhy PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-fmhy PRIVATE aethernet-protocol pthread)
add_test(NAME FmhyCatalogueTests COMMAND test-fmhy)

# aether-vault in-memory service (Phase-2): store/recover/health/replicate over
# the reed-solomon codec. A self-contained behavioural test (no JSON fixture).
add_executable(test-vault
    test_vault.c
)
target_include_directories(test-vault PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-vault PRIVATE aethernet-protocol pthread)
add_test(NAME VaultServiceTests COMMAND test-vault)

# Cross-language P-256 ECDSA verify driver. Transcribes the 3 vectors from
# tests/cross-language/p256-fixtures.json (no JSON parser on the test surface) and
# drives aethernet_ed25519_verify_with_fallback, mirroring the other-language P-256
# fixture tests. Exits non-zero if the legacy fallback regresses to Ed25519-only.
add_executable(test-p256
    test_p256.c
)
target_include_directories(test-p256 PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-p256 PRIVATE aethernet-protocol pthread)
add_test(NAME P256FixtureTests COMMAND test-p256)

# Convenience target: build every test binary in one step.
# Usage: cmake --build build --target aether-tests
add_custom_target(aether-tests
    DEPENDS
        test-tip-packet
        test-reed-solomon
        test-vault
        test-pov-token
        test-market
        test-forge
        test-space
        test-fmhy
        test-protocol
        test-routing
        test-dtn
        test-dtn-fixture
        test-sos
        test-fixtures
        test-signal-fixtures
        test-signal-session
        test-fuzz
        test-rlnc
        test-predictive-selector
        test-voice
        test-streaming
        test-group-voice
        test-video-call
        test-watch-together
        test-security
        test-transport-metrics
        test-transport-inprocess
        test-reputation
        test-anomaly
        test-gossip
        test-aether-tag
        test-uri
        test-erid
        test-chunk-bitmap
        test-directory-service
        test-bandwidth
        test-bandwidth-fixtures
        test-p256
)

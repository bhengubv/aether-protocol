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

# Convenience target: build every test binary in one step.
# Usage: cmake --build build --target aether-tests
add_custom_target(aether-tests
    DEPENDS
        test-protocol
        test-routing
        test-dtn
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
        test-chunk-bitmap
        test-directory-service
        test-bandwidth
)

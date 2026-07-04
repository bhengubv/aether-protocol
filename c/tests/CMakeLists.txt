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

add_executable(test-circuit-relay-fixture
    test_circuit_relay_fixture.c
)
target_include_directories(test-circuit-relay-fixture PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-circuit-relay-fixture PRIVATE aethernet-protocol pthread)
# Run from the repo root so the relative fixtures/circuit-relay/expected/*.bin paths resolve.
add_test(NAME CircuitRelayFixtureTests COMMAND test-circuit-relay-fixture
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Behavioural engine test: in-process 3-node mesh (A ── R ── B, no A-B edge) exercising
# the circuit-relay-v2 Transport's target/client/relay roles. Mirrors go/circuitrelay/
# transport_test.go. Run from the repo root for symmetry with the fixture tests.
add_executable(test-relay-engine
    test_relay_engine.c
)
target_include_directories(test-relay-engine PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-relay-engine PRIVATE aethernet-protocol pthread)
add_test(NAME RelayEngineTests COMMAND test-relay-engine
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Mesh-integration test: the circuit-relay-v2 engine relayed over real CircuitRelayControl
# MeshPackets via the production mesh RelayLink (c/src/relay_mesh_link.c). 3-node A ── R ── B
# topology with no direct A-B edge. Mirrors go/circuitrelay/meshlink_test.go.
add_executable(test-relay-mesh
    test_relay_mesh.c
)
target_include_directories(test-relay-mesh PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-relay-mesh PRIVATE aethernet-protocol pthread)
add_test(NAME RelayMeshTests COMMAND test-relay-mesh
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Gap-2 acceptance test: the circuit-relay engine wrapped as a generic transport
# (c/src/mesh_circuit_relay.c) is AUTO-SELECTED by the minimal transport manager
# (c/src/transport_manager.c) as the last-resort serverless fallback. 3-node A ── R ── B
# in-process hub (no A-B edge); each node's relay is built via the factory and wrapped in a
# manager as its ONLY transport; A sends via the MANAGER and B receives, tagged with the relay
# transport's name. Mirrors go/circuitrelay/manager_integration_test.go and the C#
# Relay_Is_Auto_Selected_By_TransportManager_As_Fallback. Run from the repo root for symmetry
# with the other relay tests. The test name carries the lowercase "relaytransportmanager" token
# so `ctest -R relaytransportmanager` (a case-sensitive regex) selects it.
add_executable(test-relay-transport-manager
    test_relay_transport_manager.c
)
target_include_directories(test-relay-transport-manager PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-relay-transport-manager PRIVATE aethernet-protocol pthread)
add_test(NAME relaytransportmanagerTests COMMAND test-relay-transport-manager
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-sos
    test_sos.c
)
target_include_directories(test-sos PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-sos PRIVATE aethernet-protocol pthread)
add_test(NAME SosTests COMMAND test-sos)

add_executable(test-channels
    test_channels.c
)
target_include_directories(test-channels PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-channels PRIVATE aethernet-protocol pthread)
add_test(NAME ChannelTests COMMAND test-channels)

add_executable(test-videocall
    test_videocall.c
)
target_include_directories(test-videocall PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-videocall PRIVATE aethernet-protocol pthread)
add_test(NAME VideoCallControlTests COMMAND test-videocall)

add_executable(test-prekey
    test_prekey.c
)
target_include_directories(test-prekey PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-prekey PRIVATE aethernet-protocol pthread)
add_test(NAME PreKeyExchangeTests COMMAND test-prekey)

# Media frames (VoicePtt 15 / ScreenShare 32) directed BINARY wire binding: serialize/send/handle
# behaviour. Byte-identity gates transcribe fixtures/media/vectors.json (2 voice_ptt + 2 screen_share,
# incl. all-zero + empty-payload — no JSON parser on the test surface, expected bytes inlined).
# Self-contained (FakeMeshSender directed-send capture); no working-directory override needed.
add_executable(test-media-frame
    test_media_frame.c
)
target_include_directories(test-media-frame PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-media-frame PRIVATE aethernet-protocol pthread)
add_test(NAME MediaFrameTests COMMAND test-media-frame)

# Presence (PresenceBeacon 21 / PresenceQuery 22) wire binding: serialize/broadcast/query/handle.
# Byte-identity gates transcribe fixtures/presence/vectors.json (no JSON parser on the test surface —
# the expected bytes are inlined). Self-contained (FakeMeshSender broadcast capture).
add_executable(test-presence
    test_presence.c
)
target_include_directories(test-presence PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-presence PRIVATE aethernet-protocol pthread)
add_test(NAME PresenceTests COMMAND test-presence)

# EridAnnounce (56) directed wire binding: send/handle behaviour + a re-pin of the shared
# EridAnnouncementCodec frame against fixtures/erid announcement_encode_hex (inlined). Self-contained
# (FakeMeshSender directed-send capture); no working-directory override needed.
add_executable(test-erid-announce
    test_erid_announce.c
)
target_include_directories(test-erid-announce PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-erid-announce PRIVATE aethernet-protocol pthread)
add_test(NAME EridAnnounceTests COMMAND test-erid-announce)

add_executable(test-bandwidth-wire
    test_bandwidth_wire.c
)
target_include_directories(test-bandwidth-wire PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-bandwidth-wire PRIVATE aethernet-protocol pthread)
add_test(NAME BandwidthWireTests COMMAND test-bandwidth-wire)

add_executable(test-profiles
    test_profiles.c
)
target_include_directories(test-profiles PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-profiles PRIVATE aethernet-protocol pthread)
add_test(NAME ProfileTests COMMAND test-profiles)

add_executable(test-heartbeat
    test_heartbeat.c
)
target_include_directories(test-heartbeat PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-heartbeat PRIVATE aethernet-protocol pthread)
add_test(NAME HeartbeatTests COMMAND test-heartbeat)

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

add_executable(test-bip39
    test_bip39.c
)
target_include_directories(test-bip39 PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-bip39 PRIVATE aethernet-protocol pthread)
# Run from the repo root so the relative fixtures/bip39/vectors.json path resolves.
# Test name carries the lowercase "bip39" token so `ctest -R bip39` (a
# case-sensitive regex) selects it.
add_test(NAME bip39Tests COMMAND test-bip39
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# BLE tracking-protection parity gate. Loads fixtures/bleprivacy/vectors.json and
# checks rotating Service UUID + IRK-based RPA (generate/resolve) against the C#
# reference (src/AetherNet.Security/Privacy/BlePrivacy.cs). Run from the repo
# root so the relative fixture path resolves. Test name carries the lowercase
# "bleprivacy" token so `ctest -R bleprivacy` (a case-sensitive regex) selects it.
add_executable(test-ble-privacy
    test_ble_privacy.c
)
target_include_directories(test-ble-privacy PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-ble-privacy PRIVATE aethernet-protocol pthread)
add_test(NAME bleprivacyTests COMMAND test-ble-privacy
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Panic-wipe duress-defence parity gate. Loads fixtures/panicwipe/vectors.json and
# checks the deterministic core (duress-PIN SHA-256 hash + constant-time verify,
# identity key-name manifest, prekey/signed-prekey name patterns, max_prekeys) plus
# behavioural secure_erase, against the C# reference
# (src/AetherNet.Security/Privacy/PanicWipe.cs). Run from the repo root so the
# relative fixture path resolves. Test name carries the lowercase "panicwipe" token
# so `ctest -R panicwipe` (a case-sensitive regex) selects it.
add_executable(test-panic-wipe
    test_panic_wipe.c
)
target_include_directories(test-panic-wipe PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-panic-wipe PRIVATE aethernet-protocol pthread)
add_test(NAME panicwipeTests COMMAND test-panic-wipe
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Decentralised multi-device sync parity gate. Loads fixtures/sync/vectors.json
# and checks the SyncRecord binary envelope (serialize == hex + deserialize
# round-trip), deterministic last-write-wins reconcile, and the Ed25519-signed
# DeviceLink (signed_body / signature / serialize / verify true-vs-wrong /
# round-trip), against the C# reference (src/AetherNet.Security/Sync/*.cs).
# Unlike the flat-object fixtures, this corpus is nested (reconcile[].records[]),
# so the test parses it with cJSON. cJSON is a PRIVATE dependency of
# aethernet-protocol (not on the public include/link surface), so this target
# adds the cJSON include dirs and links the cjson target explicitly. Run from the
# repo root so the relative fixture path resolves; the lowercase "sync" token in
# the test name lets `ctest -R sync` (a case-sensitive regex) select it.
add_executable(test-sync
    test_sync.c
)
target_include_directories(test-sync PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../include
    ${cjson_SOURCE_DIR}
    ${CJSON_SHIM_DIR}
)
target_link_libraries(test-sync PRIVATE aethernet-protocol pthread cjson)
add_test(NAME syncTests COMMAND test-sync
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

# Transport-backed WebRTC signalling carrier (AWS1 + JSON framing) acceptance test.
# Two SEPARATE carrier instances over an in-process loopback transport pair round-trip
# an OFFER and an ANSWER, mirroring C# RelaySignalingTests. When -DAETHERNET_WITH_WEBRTC=ON,
# it additionally drives a FULL offer/answer over two real libdatachannel WebRTC transports
# (the AETHERNET_WITH_WEBRTC=1 define propagates from the aethernet-protocol PUBLIC compile
# definition, and libdatachannel links PUBLIC through the library — so one target serves both
# levels). The lowercase "signaling" + "webrtc" tokens let `ctest -R "webrtc|signaling"`
# (a case-sensitive regex) select it. Self-contained (no JSON fixture).
add_executable(test-webrtc-signaling
    test_webrtc_signaling.c
)
target_include_directories(test-webrtc-signaling PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-webrtc-signaling PRIVATE aethernet-protocol pthread)
# Test NAME carries the lowercase "webrtc" + "signaling" tokens so the
# case-sensitive regex `ctest -R "webrtc|signaling"` selects it (the same
# lowercase-token convention used by bip39Tests / bleprivacyTests / syncTests).
add_test(NAME webrtcsignalingCarrierTests COMMAND test-webrtc-signaling)

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

# ── Phase-2 WIRE bindings (SpaceBreadcrumb 40 / ForgeAnnounce 41 / VaultShardRequest 42) ─────
# Thin broadcast+handle mesh transport for each extension. Byte-identity gates transcribe the
# canonical vectors from fixtures/{space,forge,vaultshard}/vectors.json (no JSON parser on the test
# surface — the expected bytes are inlined), mirroring the C# WirePacketsTests. Self-contained
# (FakeMeshSender capture); no working-directory override needed.

# SpaceBreadcrumb (40) wire binding: serialize/broadcast/handle behaviour.
add_executable(test-space-breadcrumb
    test_space_breadcrumb.c
)
target_include_directories(test-space-breadcrumb PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-space-breadcrumb PRIVATE aethernet-protocol pthread)
add_test(NAME SpaceBreadcrumbWireTests COMMAND test-space-breadcrumb)

# ForgeAnnounce (41) wire binding: serialize/broadcast/handle behaviour.
add_executable(test-forge-announce
    test_forge_announce.c
)
target_include_directories(test-forge-announce PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-forge-announce PRIVATE aethernet-protocol pthread)
add_test(NAME ForgeAnnounceWireTests COMMAND test-forge-announce)

# VaultShardRequest (42) wire binding: serialize/request/handle behaviour.
add_executable(test-vaultshard
    test_vaultshard.c
)
target_include_directories(test-vaultshard PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-vaultshard PRIVATE aethernet-protocol pthread)
add_test(NAME VaultShardRequestWireTests COMMAND test-vaultshard)

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

# Cross-language PeerID parity driver. Transcribes the 5 cases from
# fixtures/peerid/inputs.json (no JSON parser on the test surface) and drives
# aethernet_peer_id_from_ed25519, asserting each derived libp2p PeerID matches
# the js-libp2p-verified expected string, mirroring the other-language PeerID
# fixture tests.
add_executable(test-peerid
    test_peerid_fixture.c
)
target_include_directories(test-peerid PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-peerid PRIVATE aethernet-protocol pthread)
add_test(NAME PeerIdFixtureTests COMMAND test-peerid)

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
        test-forge-announce
        test-space
        test-space-breadcrumb
        test-vaultshard
        test-fmhy
        test-protocol
        test-routing
        test-dtn
        test-dtn-fixture
        test-relay-engine
        test-relay-mesh
        test-relay-transport-manager
        test-circuit-relay-fixture
        test-sos
        test-channels
        test-videocall
        test-prekey
        test-media-frame
        test-presence
        test-erid-announce
        test-profiles
        test-heartbeat
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
        test-webrtc-signaling
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
        test-bandwidth-wire
        test-p256
        test-peerid
        test-bip39
        test-ble-privacy
        test-panic-wipe
        test-sync
)

# Tests for Aether Protocol

add_executable(test-protocol
    test_protocol.c
)
target_include_directories(test-protocol PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-protocol PRIVATE aether-protocol pthread)
add_test(NAME ProtocolTests COMMAND test-protocol)

add_executable(test-routing
    test_routing.c
)
target_include_directories(test-routing PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-routing PRIVATE aether-protocol pthread)
add_test(NAME RoutingTests COMMAND test-routing)

add_executable(test-dtn
    test_dtn.c
)
target_include_directories(test-dtn PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-dtn PRIVATE aether-protocol pthread)
add_test(NAME DtnTests COMMAND test-dtn)

add_executable(test-sos
    test_sos.c
)
target_include_directories(test-sos PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-sos PRIVATE aether-protocol pthread)
add_test(NAME SosTests COMMAND test-sos)

add_executable(test-fixtures
    test_fixtures.c
)
target_include_directories(test-fixtures PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-fixtures PRIVATE aether-protocol pthread)
# Run from the repo root so the fixture-locator finds fixtures/.
add_test(NAME FixtureTests COMMAND test-fixtures
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

add_executable(test-signal-fixtures
    test_signal_fixtures.c
)
target_include_directories(test-signal-fixtures PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-signal-fixtures PRIVATE aether-protocol pthread)
# Run from the repo root so find_repo_root() locates AetherProtocol.slnx.
add_test(NAME SignalFixtureTests COMMAND test-signal-fixtures
    WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../..)

# Portable, dependency-free fuzz harness for the primitive entry points
# (X25519 / HKDF / HMAC). 100k smoke iterations by default; tunable via
# AETHER_FUZZ_ITERATIONS for adversarial local runs.
add_executable(test-fuzz
    test_fuzz.c
)
target_include_directories(test-fuzz PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-fuzz PRIVATE aether-protocol pthread)
add_test(NAME FuzzTests COMMAND test-fuzz)

add_executable(test-signal-session
    test_signal_session.c
)
target_include_directories(test-signal-session PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)
target_link_libraries(test-signal-session PRIVATE aether-protocol pthread)
add_test(NAME SignalSession COMMAND test-signal-session)

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
)

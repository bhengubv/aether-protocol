# Tests for Aether Protocol

add_executable(test-protocol
    test_protocol.c
)

target_include_directories(test-protocol
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/../include
)

target_link_libraries(test-protocol
    PRIVATE
        aether-protocol
        pthread
)

add_test(NAME ProtocolTests COMMAND test-protocol)

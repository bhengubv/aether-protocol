# Bench harness — runs the primitive crypto hot paths and prints a
# markdown ns/op / ops/sec table to stdout. Not in ctest; invoke as
#   cmake --build . --target bench && ./bench/bench
# Pin a baseline:
#   ./bench/bench | tee ../bench/baseline.md
add_executable(bench
    bench.c
)

target_include_directories(bench
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/../include
)

target_link_libraries(bench
    PRIVATE
        aether-protocol
        pthread
)

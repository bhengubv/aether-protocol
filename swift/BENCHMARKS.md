<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Swift Benchmarks

XCTest `measure`-block harness mirroring the C# AetherMesh.Benchmarks
suite, the Go `go/bench` harness, the Python
`python/benchmarks/test_benchmark.py`, the C `c/benchmarks/` runner,
and the TypeScript `benchmarks/bench.ts` — same eleven hot paths so a
regression in any language shows up as a delta against the committed
baseline.

## Run

From `swift/`:

```bash
swift test --filter Benchmark
```

For release-mode numbers comparable to the other languages' baselines:

```bash
swift test --filter Benchmark -c release
```

XCTest's `measure { }` block runs each test 10 times and prints mean
and standard deviation per iteration. To pin a baseline, run the suite
in Xcode (Product > Test) once with the perf-test runner — Xcode
records a baseline file you can commit and diff against on subsequent
runs.

## What we measure

Eleven cases — the hot paths every packet on the mesh traverses:

| Bench | What it pins |
| ----- | ------------ |
| `testBench_x25519Agree` | One ECDH agreement; X3DH inner loop. |
| `testBench_hkdfSha256_64Bytes` | KDF_RK per Signal §5.2. |
| `testBench_x3dhEstablish` | Full pre-key bundle process (4× X25519 + HKDF). |
| `testBench_signalEncrypt` | Steady-state Encrypt; HMAC chain + AES-GCM. |
| `testBench_signalDecrypt` | Steady-state Decrypt. |
| `testBench_packetSerialize` | Wire serialiser, 50-byte payload. |
| `testBench_packetSerializeLarge` | Wire serialiser, 10 KB payload. |
| `testBench_packetDeserialize` | Wire deserialiser. |
| `testBench_packetRoundTrip` | Serialize + Deserialize regression detector. |
| `testBench_routeStoreLookup` | Cached-route hot path. |
| `testBench_routeStoreSave` | Install a new route entry. |

## What the numbers mean

XCTest reports `mean (s)` and `stddev (s)` per measurement block. The
harness wraps an inner loop (1000 ECDH agreements, 100 encrypts, etc.)
inside each block so the per-iteration value is in the microsecond
range and the noise floor stays well below the per-call cost.

## Regression gate

Same threshold as the rest of the family: CI fails the run if `mean`
regresses by more than 25% against the saved baseline. In practice
noise on Apple-silicon CI runners is ~10%, so 25% is the smallest delta
we trust to be a real change.

## Why XCTest measure blocks

Swift has no canonical micro-benchmark framework the way Go has
`testing.B`. `XCTest.measure { }` is the stdlib-shipped facility; it
integrates with both `swift test` on the command line and Xcode's
perf-test runner with baseline-comparison built-in. No third-party
benchmark dependency is pulled into `Package.swift` — the bench is a
test target like the rest of the suite.

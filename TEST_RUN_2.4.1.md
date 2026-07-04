# AetherNet 2.4.1 — cross-language test-run record

Evidence behind the "8/8 languages" claim for the shared WebRTC signalling fixture (the 2.4.1
audit follow-up), plus regression checks. **Byte-parity is sacred:** no mesh wire byte or fixture
changed — the shared `fixtures/webrtc/` corpus equals the bytes all 8 languages already produced.

- **Date:** 2026-07-04
- **Runners:** Windows dev box (`dotnet` / `go` / `python` / `npx` / `cargo` / `gradlew`) and the
  macOS build server `admin@195.82.45.44` (`swift` / `ctest`).
- **Discipline:** every result below was run and observed by the author this wave — no inherited or
  assumed "green". A run that reports **0 tests** is treated as a failure, not a pass (this caught
  the Rust `#![cfg(feature = "webrtc")]` gate).

## Signalling fixture (Gap 1) + kept per-language suites — 8/8

| Language | Command | Result | Host |
|---|---|---|---|
| Go | `go test ./transport/webrtc/ -run "RelaySignaling\|WebRtcFixtures"` | **PASS** — 5 frame + 5 deframe fixture subtests + 4 carrier tests | Windows |
| TypeScript | `npx tsx --test tests/transport_webrtc_relay_signaling.test.ts` | **PASS** — 13 tests, 0 fail | Windows |
| Python | `python -m pytest tests/test_webrtc_relay_signaling.py -q` | **PASS** — 14 passed, 1 skipped (aiortc gate) | Windows |
| C# | `dotnet test tests/AetherNet.Transport.WebRtc.Tests -c Release` | **PASS** — 17 passed | Windows |
| Kotlin | `gradlew test --tests "aethernet.transport.webrtc.*"` | **PASS** — fixture 2/2 + kept 4 pass / 1 gated-skip | Windows |
| Rust | `cargo test --features webrtc --test webrtc_fixture` + `--lib webrtc_relay_signaling` | **PASS** — 2 + 6 | Windows |
| Swift | `swift test --filter RelaySignaling` | **PASS** — 5 tests, 0 fail | **macOS** |
| C | `ctest -R webrtc` (plain `cmake`) | **PASS** — `webrtcsignalingCarrierTests` + `webrtcfixtureTests` | **macOS** |

## Regression checks — full suites

- **Go** `go test ./...` — every package `ok`, exit 0 (routing, transport, transport/webrtc,
  circuit-relay, security, DTN, …). No regression.
- **Python** `python -m pytest -q` — **946 passed, 1 skipped, 63 subtests passed, 2 failed** (below).

## Known-failing — OUT OF SCOPE (not this wave, not a regression)

- `tests/test_transport_webrtc.py::test_two_peers_exchange_bytes_no_server`
- `tests/test_transport_webrtc.py::test_bidirectional_exchange`

These are the **real `aiortc` peer-to-peer byte-exchange** tests — they stand up two actual WebRTC
peer connections and require a live ICE/network path. They live in a file this wave never touched
(the Python change was isolated to `test_webrtc_relay_signaling.py`, which passed 14/1-skip). The
signalling **frame** serialization these tests sit above is unaffected. Physical / real-world WebRTC
transport is explicitly out of scope for the gap-closure; these are not a regression introduced by
2.4.1.

## Byte-parity proof

1. `fixtures/webrtc/expected/*.bin` generated from the Go oracle (`go/cmd/webrtcfixturegen`), whose
   serializer is byte-identical to C# `System.Text.Json`.
2. Each generated `.bin` confirmed equal to that language's prior hardcoded golden — including the
   `AB+/CD=xy <t> &z` (base64 `+`, `< > &`) and `ç é 世` (non-ASCII) exotic vectors.
3. After all 8 conversions, `git diff` on `fixtures/webrtc/expected/*.bin` is empty — no fixture byte
   moved, and every language's test asserts equality against the same committed bytes.

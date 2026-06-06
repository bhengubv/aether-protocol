<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — TypeScript Benchmarks

`tinybench` harness mirroring the C# AetherMesh.Benchmarks suite, the Go
`go/bench` harness, the Python `python/benchmarks/test_benchmark.py`,
and the C `c/benchmarks/` runner — same hot paths so a regression in
any language shows up as a delta against the committed baseline.

## Run

From `typescript/`:

```bash
npm run bench
```

The harness prints a markdown table to stdout, ready to paste into a
CI baseline diff comment. Pin a baseline by piping the output to a
file:

```bash
npm run bench > baseline.md
```

Compare a future run by diffing the new output against `baseline.md`.

## What we measure

Eleven cases — the hot paths every packet on the mesh traverses:

| Bench | What it pins |
| ----- | ------------ |
| `x25519Agree` | One ECDH agreement; X3DH inner loop. |
| `hkdfSha256_64Bytes` | KDF_RK per Signal §5.2. |
| `x3dhEstablish` | Full pre-key bundle process (4× X25519 + HKDF). |
| `signalEncrypt` | Steady-state Encrypt; HMAC chain + AES-GCM. |
| `signalDecrypt` | Steady-state Decrypt. |
| `packetSerialize` | Wire serialiser, 50-byte payload. |
| `packetSerialize_large` | Wire serialiser, 10 KB payload. |
| `packetDeserialize` | Wire deserialiser. |
| `packetRoundTrip` | Serialize + Deserialize regression detector. |
| `routeStore_lookup` | Cached-route hot path. |
| `routeStore_save` | Install a new route entry. |

## What the numbers mean

`tinybench` reports min / max / mean / median / stddev / p99 / p995 /
p999 / hz / rme for each case. The harness prints `mean (μs)`,
`p99 (μs)`, `hz`, and `rme (%)` — `mean` for the central tendency,
`p99` to catch tail latency, `hz` for throughput, and `rme` (relative
margin of error) so you can tell at a glance whether a delta is real
or noise. Below ~5% rme, deltas of 10%+ are reliably real; above 10%
rme, only large deltas (25%+) are trustworthy.

The published thresholds in CI fail the run if `mean` regresses by
more than 25% against the saved baseline — in practice noise on the
GitHub runner is ~10%, so 25% is the smallest delta we trust to be a
real change.

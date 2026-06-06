<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Go Benchmarks

`testing.B` harness mirroring the C# AetherMesh.Benchmarks suite, the
Python `python/benchmarks/` runner, the TypeScript `tinybench` harness,
and the C `c/bench/` runner — same hot paths so a regression in any
language shows up as a delta against the committed baseline.

## Run

From `go/`:

```bash
go test -bench=. ./bench/...
```

Pin a baseline (3-iteration aggregate keeps GC noise out of the
median):

```bash
go test -bench=. -benchmem -count=3 ./bench/ | tee bench/baseline.txt
```

Compare a future run by diffing the new output against
`bench/baseline.txt`. The Go `benchstat` tool is the canonical way to
quantify deltas:

```bash
go install golang.org/x/perf/cmd/benchstat@latest
benchstat bench/baseline.txt bench/new.txt
```

## What we measure

Eleven cases — the hot paths every packet on the mesh traverses:

| Bench | What it pins |
| ----- | ------------ |
| `BenchmarkX25519Agree` | One ECDH agreement; X3DH inner loop. |
| `BenchmarkHkdfSha256_64Bytes` | KDF_RK per Signal §5.2. |
| `BenchmarkX3DHEstablish` | Full pre-key bundle process (4x X25519 + HKDF). |
| `BenchmarkSignalEncrypt` | Steady-state Encrypt; HMAC chain + AES-GCM. |
| `BenchmarkSignalDecrypt` | Steady-state Decrypt. |
| `BenchmarkPacketSerialize` | Wire serialiser, 50-byte payload. |
| `BenchmarkPacketSerialize_Large` | Wire serialiser, 4 KB payload. |
| `BenchmarkPacketDeserialize` | Wire deserialiser. |
| `BenchmarkPacketRoundTrip` | Serialize + Deserialize regression detector. |
| `BenchmarkRouteStore_Lookup` | Cached-route hot path. |
| `BenchmarkRouteStore_Save` | Install a new route entry. |

## Sample numbers

Smoke run from the bench-landing commit (`f873543`) — `-benchtime=1x`,
single iteration per case, just enough to confirm the harness compiles
and produces signal:

| Case | ns/op |
| ---- | ----: |
| BenchmarkX25519Agree | ~42,000 |
| BenchmarkHkdfSha256_64Bytes | ~24,000 |
| BenchmarkX3DHEstablish | ~519,000 |
| BenchmarkSignalEncrypt | ~3,700 |
| BenchmarkSignalDecrypt | ~8,000 |

Steady-state Encrypt at ~3.7 us/op means a mid-range laptop core can
push ~270k Signal-encrypted messages per second through the chain step
+ AES-GCM hot path. X3DH at ~519 us/op caps session-establishment
throughput at ~2k peers per second per core, which is comfortably above
the per-node peering rate any mesh router will see.

## What the numbers mean

`testing.B` reports `ns/op` (wall-clock per iteration) and, with
`-benchmem`, `B/op` and `allocs/op`. The CI gate fails the run if the
mean regresses by more than 25% against the saved baseline — in
practice noise on a GitHub runner is ~10%, so 25% is the smallest delta
we trust to be a real change.

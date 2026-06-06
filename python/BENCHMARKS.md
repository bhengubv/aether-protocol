<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Python Benchmarks

`pytest-benchmark` harness mirroring the C# AetherMesh.Benchmarks suite
and the Go `go/bench` harness — same hot paths so a regression in any
language shows up as a delta against the committed baseline.

## Run

From `python/`:

```bash
python -m pytest benchmarks/ --benchmark-only -q
```

Pin a baseline:

```bash
python -m pytest benchmarks/ --benchmark-only \
    --benchmark-save=python_baseline -q
```

Compare a future run against the saved baseline:

```bash
python -m pytest benchmarks/ --benchmark-only \
    --benchmark-compare=python_baseline -q
```

## What we measure

Eleven cases — the hot paths every packet on the mesh traverses:

| Bench | What it pins |
| ----- | ------------ |
| `bench_x25519_agree` | One ECDH agreement; X3DH inner loop. |
| `bench_hkdf_sha256_64bytes` | KDF_RK per Signal §5.2. |
| `bench_x3dh_establish` | Full pre-key bundle process (4x X25519 + HKDF). |
| `bench_signal_encrypt` | Steady-state Encrypt; HMAC chain + AES-GCM. |
| `bench_signal_decrypt` | Steady-state Decrypt. |
| `bench_packet_serialize` | Wire serialiser, 50-byte payload. |
| `bench_packet_serialize_large` | Wire serialiser, 10KB payload. |
| `bench_packet_deserialize` | Wire deserialiser. |
| `bench_packet_round_trip` | Serialize + Deserialize regression detector. |
| `bench_route_store_lookup` | Cached-route hot path. |
| `bench_route_store_save` | Install a new route entry. |

## What the numbers mean

`pytest-benchmark` reports min / max / mean / median / stddev / IQR for
each case, plus operations-per-second. The most stable metric for
regression detection is `median` (resistant to GC pauses and OS
scheduling jitter). The published thresholds in CI fail the run if
`mean` regresses by more than 25% against the saved baseline — in
practice noise on the GitHub runner is ~10%, so 25% is the smallest
delta we trust to be a real change.

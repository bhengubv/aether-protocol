<!-- SPDX-License-Identifier: MIT -->

# aether-protocol — Kotlin Benchmarks

`kotlinx-benchmark` harness (over JMH 1.37) mirroring the C#
`Aether.Benchmarks` suite, the Go `go/bench` harness, the Python
`python/benchmarks/test_benchmark.py`, the C `c/benchmarks/` runner,
and the TypeScript `typescript/benchmarks/bench.ts` — same hot paths so
a regression in any language shows up as a delta against the committed
baseline.

## Run

From `kotlin/`:

```bash
./gradlew benchmark
```

The harness prints a JMH-format summary table to stdout. Pin a baseline
by piping the output to a file:

```bash
./gradlew benchmark | tee bench/baseline.txt
```

Diff a future run against `bench/baseline.txt` to spot regressions.

## What we measure

Eleven cases — the hot paths every packet on the mesh traverses (same
shape and ordering as the other-language harnesses):

| Bench | What it pins |
| ----- | ------------ |
| `x25519Agree` | One ECDH agreement; X3DH inner loop. |
| `hkdfSha256_64Bytes` | KDF_RK per Signal §5.2. |
| `x3dhEstablish` | Full pre-key bundle process (4× X25519 + HKDF). |
| `signalEncrypt` | Steady-state Encrypt; HMAC chain + AES-GCM. |
| `signalDecrypt` | Steady-state Decrypt (encrypt is included — see note). |
| `packetSerialize` | Wire serialiser, 50-byte payload. |
| `packetSerialize_large` | Wire serialiser, 10 KB payload. |
| `packetDeserialize` | Wire deserialiser. |
| `packetRoundTrip` | Serialize + Deserialize regression detector. |
| `routeStore_lookup` | Cached-route hot path. |
| `routeStore_save` | Install a new route entry. |

`signalDecrypt` includes a fresh `encrypt` step inside the measured
window — the receive ratchet advances on each call so the same
ciphertext can't be replayed across iters, and isolating decrypt would
require an out-of-band ratchet-state rewind that `SignalProtocol` does
not expose. Same approach as the C# / TypeScript benches; subtract the
`signalEncrypt` mean to estimate the pure decrypt cost.

## Configuration

`build.gradle.kts` configures the `main` configuration with:

- `warmups = 3` — JMH warmup iterations, discarded.
- `iterations = 5` — JMH measurement iterations.
- `iterationTime = 500 ms` — wall clock per iteration.
- `mode = "avgt"` — average time per op (microseconds, set via
  `outputTimeUnit`).
- `reportFormat = "text"` — JMH text-format summary to stdout.

Total bench wall clock: ~1 minute on a recent JVM. Tighter signal needs
more iterations:

```bash
./gradlew benchmark -PbenchmarkConfiguration=tight
```

(after wiring a `tight` configuration in `build.gradle.kts` with
`iterations = 20`, `iterationTime = 1 s`).

## What the numbers mean

JMH reports `Score` (mean per op) and `Score Error (99.9%)` (confidence
interval) for each case. Default output is `Score ± Error  Units`
(microseconds in our config).

The published threshold in CI fails the run if `Score` regresses by
more than 25% against the saved baseline — in practice JVM noise on
the GitHub runner is ~10%, so 25% is the smallest delta we trust to be
a real change.

## Why JMH and not a hand-rolled timer

JMH handles the JVM-specific traps that bite hand-rolled benches:

- Dead-code elimination — JMH consumes returned values via `Blackhole`
  so the JIT can't optimise the call away.
- Warmup — the first ~10k iterations of any new code path on the JVM
  are running interpreted or in C1; JMH discards them so the measured
  window only sees the C2-compiled steady state.
- On-stack replacement — JMH controls the loop shape so OSR doesn't
  trigger mid-measurement and skew one iteration relative to the rest.

The trade-off: JMH benches cost ~1 min total wall clock for our
default config. The TypeScript `tinybench` harness runs in ~6 s by
comparison, but tinybench only protects against the cheapest of the
three problems above. For the JVM, JMH is the right tool.

# Aether Benchmarks

BenchmarkDotNet harness for the C# crypto and serializer hot paths.

## Why this exists

Every packet on the Aether mesh is signed, verified, possibly encrypted via
Signal X3DH + Double Ratchet, and serialized to the wire format on send /
deserialized on every hop's receive. A regression of a few microseconds in any
of these paths multiplies across every router and every link in the mesh.

The benchmarks here pin baseline numbers for the per-message overhead so that
future regressions — from BCL upgrades, BouncyCastle / NSec version bumps, or
refactors — are visible.

## What's covered

| Class | Hot path |
| --- | --- |
| `PacketSerializerBenchmarks` | `MeshPacket` wire format, small + 4 KiB payloads, round-trip. |
| `SignalProtocolBenchmarks`   | X3DH bundle processing, first PreKey message, subsequent messages, normal decrypt, full DH-ratchet step. |
| `PrimitivesBenchmarks`       | X25519 ECDH agreement, HMAC-SHA256 (1-byte input — the ratchet inner loop), HKDF-SHA256 (KDF_RK, 64 bytes), Ed25519 sign + verify. |

## Running

Run the whole suite:

```bash
dotnet run -c Release --project bench/Aether.Benchmarks --filter '*'
```

Run a specific class:

```bash
dotnet run -c Release --project bench/Aether.Benchmarks --filter '*PacketSerializer*'
dotnet run -c Release --project bench/Aether.Benchmarks --filter '*SignalProtocol*'
dotnet run -c Release --project bench/Aether.Benchmarks --filter '*Primitives*'
```

Run a single method:

```bash
dotnet run -c Release --project bench/Aether.Benchmarks --filter '*Encrypt_SubsequentMessage*'
```

List all discovered benchmarks without running them:

```bash
dotnet run -c Release --project bench/Aether.Benchmarks -- --list flat
```

> Always pass `-c Release`. BenchmarkDotNet warns if invoked under Debug and the
> numbers are unrepresentative.

## Reading the results

BenchmarkDotNet prints a Markdown summary table at the end of each run and
also writes detailed reports under `bench/Aether.Benchmarks/BenchmarkDotNet.Artifacts/`.

The `[MemoryDiagnoser]` attribute on every class adds three columns to the
output: `Allocated`, `Gen0`, `Gen1` — track these alongside `Mean` to catch
allocation regressions that don't show up in wall-clock time.

## Notes on methodology

* Every benchmark sets up state in `[GlobalSetup]` so the timed region only
  exercises the hot path, not the harness overhead.
* `SignalProtocolBenchmarks` uses `[IterationSetup]` for benchmarks that
  consume one-time pre-keys or advance ratchet counters per call.
* `PrimitivesBenchmarks` uses reflection to reach `X25519Service`, which is
  `internal` to `Aether.Security`. This avoids modifying the production project's
  `InternalsVisibleTo` list. If `X25519Service` is ever made public or its
  signature changes, update `ResolveX25519Method` accordingly.
* Don't run the benchmark suite on a battery / power-saving profile. CPU
  frequency scaling will distort the numbers.

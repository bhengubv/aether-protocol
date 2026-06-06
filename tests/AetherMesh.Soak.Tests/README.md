# AetherMesh.Soak.Tests

Long-running soak tests for the Aether crypto + messaging stack. These run
the hot paths (Signal encrypt/decrypt, DH-ratchet steps, packet signing,
DTN bundle expiry, routing churn, OPK pool replenishment) for thousands of
iterations and measure memory growth + cache bounds. They surface bugs
that point-in-time unit tests miss: slow leaks, key-zeroing failures,
session-state bloat, dictionary growth past the freshness window.

The standard unit suite (`tests/AetherMesh.Core.Tests`) is unaffected — soak
tests live in this separate project so adopters can opt-in for CI without
slowing the main `dotnet test` invocation.

## Running

Default iteration count (~30 s on a modern dev laptop):

```sh
dotnet test tests/AetherMesh.Soak.Tests/AetherMesh.Soak.Tests.csproj -c Release --nologo
```

Long-form soak (CI / nightly):

```sh
AETHERMESH_SOAK_ITERATIONS=100000 dotnet test tests/AetherMesh.Soak.Tests/... -c Release --nologo
```

On Windows PowerShell:

```powershell
$env:AETHERMESH_SOAK_ITERATIONS = 100000
dotnet test tests/AetherMesh.Soak.Tests/AetherMesh.Soak.Tests.csproj -c Release --nologo
```

Filter to soak only (the trait is `Category=Soak`):

```sh
dotnet test tests/AetherMesh.Soak.Tests/... --filter "Category=Soak"
```

To skip soak when running everything:

```sh
dotnet test --filter "Category!=Soak"
```

## What each test exercises

| File | Hot path | Bound checked |
| --- | --- | --- |
| `SignalEncryptDecryptSoakTests` | `SignalProtocolService.EncryptAsync` / `DecryptAsync` (default 10k) | Per-iter < 1 KB; total < 5 MB |
| `SignalEncryptDecryptSoakTests` | DH-ratchet step (1k full roundtrips) | Per-iter < 4 KB |
| `PacketSigningSoakTests` | `PacketSigningService.SignPacketAsync` + verify | Per-iter < 512 B; dedup retains across run |
| `PacketSigningSoakTests` | `Dispose()` releases the cleanup timer | Service is GC-collectable |
| `RoutingSoakTests` | Route install + TTL + prune | Store empties after expiry + prune |
| `DtnSoakTests` | Bundle TTL → `ExpireStaleAsync` | All bundles transition to Expired |
| `DtnSoakTests` | Sustained `CreateBundleAsync` | Per-iter < 4 KB |
| `MessagingSoakTests` | Full send pathway (Signal cipher + outbox) | Per-iter < 8 KB; outbox = sent count |
| `PreKeyPoolSoakTests` | 200 concurrent initiators consume OPKs | All ids distinct; pool tops up |
| `PreKeyPoolSoakTests` | Long-run X3DH consume + replenish | Available count steady; held ≤ 2× pool |

## Iteration count

`AETHERMESH_SOAK_ITERATIONS` overrides the per-test default. Some tests cap at
a sensible smaller value (1 000 for ratchet / DTN, 200 for concurrent
initiators) — those caps stay in effect even with a higher env var, since
those workloads have a quadratic factor (full roundtrip per iteration) or
a fixed natural ceiling.

## What the tests do NOT do

* No synthetic-clock injection. `RoutingService` and `DtnService` use
  `DateTime.UtcNow` directly, and forcing time forward for soak runs would
  require wider refactor. TTL paths are exercised with short real-time
  TTLs (~500 ms – 1 s) instead.
* No PacketSigningService timer eviction test. The cleanup timer fires
  every 60 s; the default-iteration soak completes in under 30 s, so the
  timer never gets a chance. The dedup-cache retention is asserted
  positively (replay of the original packet still rejected at end-of-run);
  full timer coverage lives in `PacketSigningServiceTests` in the unit
  suite.
* No fairness or starvation tests. These soaks measure leak / bound
  invariants; latency-distribution work belongs in
  `bench/AetherMesh.Benchmarks/`.

## Adding a new soak test

1. Inherit from `SoakTestBase`.
2. Tag the class with `[Trait("Category", "Soak")]` so the filter works.
3. Use `MeasureMemoryGrowthAsync` / `MeasureMemoryGrowth` for the
   measurement window — it forces a deterministic full GC on both ends so
   results are run-to-run stable.
4. Resolve iteration count via `ResolveIterations(defaultIterations)` —
   never hard-code.
5. Call `WriteSummary(...)` at the end so the throughput + GC numbers
   land in the runner's output (`dotnet test --logger:console;verbosity=detailed`
   surfaces them).

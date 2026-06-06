# Benchmark Baselines

Each file records the per-case median throughput (ns/op) for a single language.
The `.github/workflows/benchmarks.yml` job reads these files and fails if any
case regresses by more than **20 %** vs the stored baseline.

## File format

```
# bench_name=<median_ns>   (one line per case; lines starting with # are ignored)
x25519_agree=98
hkdf_sha256_64bytes=245
```

A file whose first non-comment line reads `PENDING` has no baseline yet — the
CI comparison step skips it and exits 0 with a warning.

## Establishing / updating a baseline

Run the helper script from the repo root — it builds each language's bench
suite, captures the output, and overwrites the baseline files:

```bash
bash scripts/update_baselines.sh
```

Then commit the updated files.  The script requires the full toolchain matrix
to be installed locally (Go 1.22, Rust stable, Python 3.12, Node 20, .NET 10,
JDK 21, Swift 5.10, GCC + libsodium).

## Baseline files

| File | Language | Bench runner |
|---|---|---|
| `go.txt` | Go | `go test -bench=. -benchmem -count=3 ./bench/` |
| `rust.txt` | Rust | `cargo bench --bench aether` (criterion median) |
| `python.txt` | Python | `pytest benchmarks/ --benchmark-json` |
| `typescript.txt` | TypeScript | `npx tsx benchmarks/bench.ts` |
| `csharp.txt` | C# | BenchmarkDotNet JSON export |
| `kotlin.txt` | Kotlin | JMH (`./gradlew jmh`) |
| `swift.txt` | Swift | XCTest `measure {}` |
| `c.txt` | C | wall-clock (`./build/bench/aethermesh_bench`) |

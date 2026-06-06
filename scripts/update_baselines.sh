#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# update_baselines.sh — Run all benchmark suites and overwrite bench/baselines/*.txt
#
# Usage (from repo root):
#   bash scripts/update_baselines.sh
#
# Requires the full toolchain matrix to be installed:
#   Go 1.22+, Rust stable, Python 3.12+, Node 20+, .NET 10,
#   JDK 21, Swift 5.10+, GCC + cmake + libsodium

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BASELINE_DIR="$REPO_ROOT/bench/baselines"
SCRIPT="$REPO_ROOT/scripts/check_bench_regression.py"
TMP=$(mktemp -d)

cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "── Aether benchmark baseline update ──────────────────────────────────────"
echo "Repo: $REPO_ROOT"
echo "Baselines: $BASELINE_DIR"
echo ""

# ── Go ──────────────────────────────────────────────────────────────────────
echo "[go] Running benchmarks..."
(
  cd "$REPO_ROOT/go"
  go test -bench=. -benchmem -count=3 ./bench/ > "$TMP/go.txt" 2>&1
) && echo "[go] Done." || echo "[go] FAILED — skipping baseline update."
if [ -s "$TMP/go.txt" ]; then
  python3 "$SCRIPT" --lang go --current "$TMP/go.txt" \
    --baseline "$BASELINE_DIR/go.txt" --save-baseline
fi

# ── Rust ────────────────────────────────────────────────────────────────────
echo "[rust] Running benchmarks..."
(
  cd "$REPO_ROOT/rust"
  cargo bench --bench aether 2>&1 > "$TMP/rust.txt"
) && echo "[rust] Done." || echo "[rust] FAILED — skipping baseline update."
if [ -s "$TMP/rust.txt" ]; then
  python3 "$SCRIPT" --lang rust --current "$TMP/rust.txt" \
    --baseline "$BASELINE_DIR/rust.txt" --save-baseline
fi

# ── Python ──────────────────────────────────────────────────────────────────
echo "[python] Running benchmarks..."
(
  cd "$REPO_ROOT/python"
  pip install -e ".[dev]" -q
  python -m pytest benchmarks/ --benchmark-json="$TMP/python.json" -q 2>&1
) && echo "[python] Done." || echo "[python] FAILED — skipping baseline update."
if [ -s "$TMP/python.json" ]; then
  python3 "$SCRIPT" --lang python --current "$TMP/python.json" \
    --baseline "$BASELINE_DIR/python.txt" --save-baseline
fi

# ── TypeScript ───────────────────────────────────────────────────────────────
echo "[typescript] Running benchmarks..."
(
  cd "$REPO_ROOT/typescript"
  npm ci -q
  npx tsx benchmarks/bench.ts > "$TMP/typescript.txt" 2>&1
) && echo "[typescript] Done." || echo "[typescript] FAILED — skipping baseline update."
if [ -s "$TMP/typescript.txt" ]; then
  python3 "$SCRIPT" --lang typescript --current "$TMP/typescript.txt" \
    --baseline "$BASELINE_DIR/typescript.txt" --save-baseline
fi

# ── C# ───────────────────────────────────────────────────────────────────────
echo "[csharp] Running benchmarks..."
(
  cd "$REPO_ROOT"
  dotnet run -c Release --project bench/AetherMesh.Benchmarks -- \
    --exporters json --artifacts "$TMP/csharp_artifacts" 2>&1 | \
    tee "$TMP/csharp_stdout.txt"
  # Extract from BenchmarkDotNet JSON exports
  python3 - <<'PYEOF'
import json, glob, sys, os

arts = os.environ.get("CSHARP_ARTS", "$TMP/csharp_artifacts")
lines = []
for f in glob.glob(f"{arts}/**/*.json", recursive=True):
    try:
        data = json.loads(open(f).read())
        for b in data.get("Benchmarks", []):
            name = b.get("FullName", b.get("Method", ""))
            stat = b.get("Statistics", {})
            median_ns = stat.get("Median", 0)
            if name and median_ns:
                lines.append(f"{name}: {median_ns:.3f} ns")
    except Exception:
        pass
if lines:
    print("\n".join(lines))
PYEOF
) > "$TMP/csharp.txt" 2>&1 && echo "[csharp] Done." || echo "[csharp] FAILED — skipping baseline update."
if [ -s "$TMP/csharp.txt" ]; then
  python3 "$SCRIPT" --lang csharp --current "$TMP/csharp.txt" \
    --baseline "$BASELINE_DIR/csharp.txt" --save-baseline
fi

# ── Kotlin ───────────────────────────────────────────────────────────────────
echo "[kotlin] Running benchmarks..."
(
  cd "$REPO_ROOT/kotlin"
  ./gradlew jmh --no-daemon 2>&1 | tee "$TMP/kotlin.txt"
) && echo "[kotlin] Done." || echo "[kotlin] FAILED — skipping baseline update."
if [ -s "$TMP/kotlin.txt" ]; then
  python3 "$SCRIPT" --lang kotlin --current "$TMP/kotlin.txt" \
    --baseline "$BASELINE_DIR/kotlin.txt" --save-baseline
fi

# ── Swift ────────────────────────────────────────────────────────────────────
echo "[swift] Running benchmarks..."
(
  cd "$REPO_ROOT/swift"
  swift test --filter BenchmarkTests 2>&1 | tee "$TMP/swift.txt"
) && echo "[swift] Done." || echo "[swift] FAILED — skipping baseline update."
if [ -s "$TMP/swift.txt" ]; then
  python3 "$SCRIPT" --lang swift --current "$TMP/swift.txt" \
    --baseline "$BASELINE_DIR/swift.txt" --save-baseline
fi

# ── C ────────────────────────────────────────────────────────────────────────
echo "[c] Running benchmarks..."
(
  cd "$REPO_ROOT/c"
  cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -S . -B build-bench > /dev/null 2>&1
  cmake --build build-bench > /dev/null 2>&1
  ./build-bench/bench/aethermesh_bench 2>&1 | tee "$TMP/c.txt"
) && echo "[c] Done." || echo "[c] FAILED — skipping baseline update."
if [ -s "$TMP/c.txt" ]; then
  python3 "$SCRIPT" --lang c --current "$TMP/c.txt" \
    --baseline "$BASELINE_DIR/c.txt" --save-baseline
fi

echo ""
echo "── Baseline update complete ───────────────────────────────────────────────"
echo "Files written to $BASELINE_DIR"
echo "Commit the updated baseline files to track them in source control."

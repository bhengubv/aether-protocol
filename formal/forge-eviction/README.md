# Forge Cache Eviction — Fairness

## What This Proves

`IForgeMeshService` caches packages with LRU eviction. This model
proves no package is starved indefinitely under bounded cache size.

| Property | Status |
|---|---|
| Cache bounded (no overflow) | ✅ |
| LRU eviction monotonic | ✅ |
| No starvation (any cached package eventually evicted, not held forever) | ✅ |

## Files

- `forge-eviction.pnml` | `.q` | `properties.md` | `state-space.md`

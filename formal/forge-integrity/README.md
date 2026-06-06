# Forge Cache — Hash Integrity

## What This Proves

`IForgeMeshService` caches packages. Proves cached package hash always
equals original hash — no corruption-in-transit possible.

| Property | Status |
|---|---|
| Stored hash matches original | ✅ |
| Tampered packages detected (hash mismatch → re-fetch) | ✅ |

## Files

`forge-integrity.pnml` | `.q` | `properties.md` | `state-space.md`

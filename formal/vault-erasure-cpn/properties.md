# Vault CPN — Stronger Properties

## P1 — Per-Document Recovery
For each doc d, recovery requires ≥ K=2 same-doc shards. Cross-doc
shards do NOT contribute to recovery. ✓ (by colour matching on `d`)

## P2 — No Shard Substitution
A shard (d1, i) cannot be used to recover doc d2 because the arc
binding requires the same `d` variable across input shards.

## P3 — Per-Shard-Index Tracking
Recovery requires SHARD_IDs 1 AND 2 (or any K-subset by arc pattern).
Catches "got 2 copies of shard 1 instead of shards 1 and 2."

## Verification
CPN Tools 4 → State Space → Calculate → Standard Properties.
Auto-derives per-doc place-flow invariants.

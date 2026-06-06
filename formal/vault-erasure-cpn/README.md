# Vault Erasure — Coloured Petri Net Upgrade

## What This Adds Over the P/T Net

Base `vault-erasure/` proves K-of-N recoverability count-wise. This
CPN adds shard identity, proving K-of-N recoverability **per specific
shard set**: any specific K-subset of N reconstructs (not just "any K
shards").

## Colour Sets

```
colset SHARD_ID  = INT with 1..14;        (* N = 14 in production *)
colset NODE_ID   = with N1 | N2 | N3 | ... | N14;
colset SHARD     = product SHARD_ID * NODE_ID;
colset DOC_ID    = with Doc1 | Doc2 | Doc3;
```

Per-document, per-shard-index. Catches:
- Shard substitution attacks (relay swaps shard 5 for shard 5 from a different document)
- Misattribution at recovery (collected shards 1, 2, 3 from Doc1 vs Doc2)
- Re-sharing identity verification

## Files

`vault-erasure.cpn` | `properties.md` | `state-space.md` | `README.md`

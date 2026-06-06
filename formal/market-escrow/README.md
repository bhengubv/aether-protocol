# Market Escrow — Atomic Vault Release

## What This Proves

`IMarketMeshService` escrows a document in `IVaultMeshService` while
buyer/seller settle. This model proves vault release happens iff
funds transfer (atomicity); dispute resolution always terminates.

| Property | Status |
|---|---|
| Vault released iff funds transferred (atomicity) | ✅ |
| Dispute path reaches resolved or refunded | ✅ |
| No state where buyer paid but seller didn't deliver | ✅ |

## Files

- `market-escrow.pnml` | `.q` | `properties.md` | `state-space.md`

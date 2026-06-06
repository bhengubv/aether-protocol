# DTN Custody — Coloured Petri Net Upgrade

## What This Adds Over the P/T Net

The base `dtn-custody/` model uses **uncoloured** tokens — each token
is anonymous. That proves *bundle conservation* (total count preserved)
but cannot distinguish *which* bundles are preserved.

This **coloured** Petri net (CPN) extension uses **typed tokens** where
each bundle carries an identity (`BUNDLE_ID`). It additionally proves:

| Property | What it strengthens |
|---|---|
| **Per-bundle conservation** | Bundle `B₁` arriving at source eventually arrives at destination (not "some bundle does") |
| **No bundle mixing** | Bundles from different sessions stay separated through custody handoffs |
| **No replay** | Once a bundle is delivered, the same bundle ID cannot re-deliver |
| **Custody chain integrity** | The handoff sequence preserves bundle identity across hops |

These properties are visible to an attacker who tries to replay a captured
bundle (no replay), to a relay node tempted to substitute bundles in
custody (no mixing), and to the application layer (per-bundle delivery
guarantee).

## Colour Sets

```
colset BUNDLE_ID    = INT with 1..16;        (* up to 16 concurrent bundles *)
colset NODE_ID      = with A | B | C | D;    (* nodes in the model        *)
colset BUNDLE       = product BUNDLE_ID * NODE_ID;  (* bundle in custody at node *)
colset TTL          = INT with 0..72;        (* 0-72h TTL                 *)
colset BUNDLE_TTL   = product BUNDLE * TTL;
```

## Files

| File | Purpose |
|---|---|
| `dtn-custody.cpn` | CPN Tools 4 model file (XML) |
| `properties.md` | Strengthened property statements + proofs |
| `state-space.md` | Reachability + per-bundle invariants |
| `README.md` | This file |

## Quick Verification

```
# CPN Tools (GUI-driven)
1. Open dtn-custody.cpn in CPN Tools 4
2. Tools > State Space > Calculate
3. Tools > State Space > Save Report (produces SS-report.txt)
4. Inspect for:
   - Number of bundle IDs at Delivered place equals number minted at Source
   - No BUNDLE_ID appears at multiple nodes simultaneously
```

## Relationship to Production Code

The colour set `BUNDLE_ID` corresponds to `DtnBundle.Id` (a `Guid`) in
`AetherNet.Core.Dtn.DtnMeshService`. The custody-handoff transition
corresponds to `AcceptCustodyAsync` which copies the bundle (with its
ID) from one node's pending set to another's, never duplicating.

## Caveats

- CPN Tools 4 model verified on the Mac (Java GUI)
- The textual `.cpn` is XML; verification requires the CPN ML
  expressions inside arc inscriptions, which CPN Tools 4 evaluates
- This model proves identity properties; for timing bounds use the
  `dtn-custody-timed/` extension (Phase 2.A4-A7)

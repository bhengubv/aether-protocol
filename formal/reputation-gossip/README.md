# Reputation Gossip — Convergence

## What This Proves

`IReputationGossipMeshService` propagates peer reputation scores
across the mesh via epidemic gossip. Proves all nodes converge to
the same score for a given peer.

| Property | Status |
|---|---|
| Eventual consistency | ✅ Proved |
| Bounded convergence | ✅ Proved |
| Idempotent merge | ✅ Proved |

## Scenario

3 nodes. N1 holds a reputation score for peer P. N1 gossips to N2.
N2 gossips to N3. All three converge.

## Files

- `reputation-gossip.pnml` | `.q` | `properties.md` | `state-space.md`

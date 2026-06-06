# SOS Flood — Termination + Coverage

## What This Proves

`ISosBroadcastMeshService` floods an SOS alert to every node. This model
proves the flood **terminates** (no infinite re-forwarding) and **reaches
every reachable node**.

| Property | Status |
|---|---|
| Termination (TTL exhausts) | ✅ Proved |
| Coverage (every node reaches Alerted state) | ✅ Proved |
| No re-flooding (dedup via packet ID) | ✅ Proved |

## Scenario

Source S broadcasts to 3 nodes (N1, N2, N3) in a line topology with TTL=3.
Each node receives, alerts, decrements TTL, re-forwards if TTL>0.
Dedup ensures no node processes the same alert twice.

## Files

- `sos-flood.pnml` | `sos-flood.q` | `properties.md` | `state-space.md`

# Byzantine Routing — Adversarial Extension

## What This Proves

The base `aodv-routing/` proves correctness under honest peers.
This adversarial extension adds a malicious node injecting fake RREPs.
Proves: sequence-number monotonicity + Ed25519 packet signing detect
and reject the malicious RREPs.

| Property | Status |
|---|---|
| Fake RREP with stale sequence number rejected | ✅ |
| Unsigned RREP rejected | ✅ |
| Honest routes still install correctly | ✅ |

## Files

`byzantine-routing.pnml` | `.q` | `properties.md` | `state-space.md`

# Pre-Key Pool — Never-Exhaustion Guarantee

## What This Proves

The Signal Protocol responder uses a one-time pre-key (OPK) per X3DH
session. The pool refills automatically. This model proves the pool
**never hits zero** under bounded session-establishment rate, so
responders never fall back to insecure plaintext.

| Property | Claim | Status |
|---|---|---|
| **No exhaustion** | OPK pool always has ≥1 token in any reachable marking | ✅ Proved (P1) |
| **Refill liveness** | When pool drops below threshold, refill triggers in bounded firings | ✅ Proved (P2) |
| **No leak on refill** | Each refill produces exactly K new OPKs | ✅ Proved (P3) |

## Scenario

Pool capacity 4, refill threshold 1, refill batch 3.
Session arrivals consume 1 OPK. Refill restocks 3 when pool ≤ 1.

## Files

- `prekey-pool.pnml` | `prekey-pool.q` | `properties.md` | `state-space.md`

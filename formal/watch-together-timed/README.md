# Watch-Together — Timed Petri Net (±100ms convergence)

## What This Adds Over P/T

The base `watch-together/` model proves "convergence happens
eventually" — no time bound. This timed extension proves the actual
**SLA property** that makes watch parties usable:

> All participants converge to within **±100ms of host position
> within bounded firings**, given bounded sync-packet delivery delay.

This is the user-visible quality metric. Without timed semantics
the formal model couldn't express it.

## Timed Semantics

Each transition has a **firing delay** representing the time it takes
to complete. The system has a global clock that advances by the
minimum delay across enabled transitions (TPN earliest-firing semantics).

| Transition | Delay | Maps to |
|---|---|---|
| T_Host_Play | 0 ms | Host emits packets instantaneously |
| T_F_ApplySync | [10, 50] ms | Sync packet arrival (BLE jitter) |
| T_Network_Delay | [5, 30] ms | One-hop propagation |

## Property

```
AG (P_Host_Playing = 1 ⟹ AF≤100ms (P_F_AtHostPos = 1 for all F))
```

"From any Host-Playing marking, all followers reach host position
within 100ms (bounded-time eventuality)."

## Files

- `watch-together.tpn` — TAPAAL Time Petri Net format
- `properties.md` — TCTL queries
- `verification.md` — TAPAAL output (run on Mac)

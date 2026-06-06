# Watch-Together — State Space

## Reachability

```
M₀ ─T_Host_Play─► M₁ (host playing, syncs in flight)
                   │
                   ├─T_F1_ApplySync─► M₂ (F1 synced)
                   │                    │
                   │                    └─T_F2_ApplySync─► M_goal
                   │
                   └─T_F2_ApplySync─► M₃ (F2 synced)
                                        │
                                        └─T_F1_ApplySync─► M_goal
```

Two orderings reach M_goal in exactly 3 transitions. ✓

## Idempotent paths

```
M_goal ─T_Host_Play─?─► (host is no longer in Paused, transition disabled) ✓
```

No way to re-emit syncs without resetting host. Once synced, stays synced.

## Verification: load .pnml + .q in TAPAAL — all 5 queries SATISFIED.

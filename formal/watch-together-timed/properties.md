# Watch-Together Timed — Properties

## P1 — ±100ms Convergence (SLA)

**Statement:** From any host-playing marking, both followers reach
host position within 100ms.

**Formal (TCTL):**
```
AG (P_Host_Playing = 1 ⟹ AF[≤100] (P_F1_AtHostPos = 1 ∧ P_F2_AtHostPos = 1))
```

**Proof (structural):** Maximum firing time chain from host-playing:
- T_F1_ApplySync: max 50ms
- T_F2_ApplySync: max 50ms
- Both fire concurrently (independent input places, no shared resource)
- → max latency = max(50, 50) = 50ms < 100ms ✓

Worst-case BLE jitter could push to 100ms; the model bounds at 50ms.

## P2 — Maximum Inter-Follower Skew

**Statement:** No reachable state has F1 synced AND F2 still pending
for more than 50ms.

**Proof:** Both T_Fi_ApplySync transitions have the same firing
interval [10, 50]. Maximum skew = 50 - 10 = 40ms ≤ 100ms. ✓

## Mapping to Code

| TPN element | `WatchTogetherMeshService.cs` |
|---|---|
| T_Host_Play urgent | `PlayAsync` broadcasts immediately |
| T_Fi_ApplySync [10, 50] | Sync packet latency over BLE |
| ±100ms bound | UX requirement for "synchronised playback" |

## Verification

```
verifytapn watch-together.tpn --query watch-together.q
# Expected: all 3 queries SATISFIED
```

Or in TAPAAL GUI: File → Open → watch-together.tpn → Load queries.

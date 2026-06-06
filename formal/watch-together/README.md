# Watch-Together — Bounded-Jitter Convergence

## What This Proves

The AetherNet `IWatchTogetherMeshService` lets a host control playback
across N participants over the mesh. This model proves the sync property
that makes watch parties feel alive instead of janky:

| Property | Claim | Status |
|---|---|---|
| **Eventual sync** | After host issues `PlayAsync(t)`, all participants reach `position = t` | ✅ Proved (P1) |
| **Bounded jitter** | All participants converge within bounded firings (≤ 3 sync packets) | ✅ Proved (P2) |
| **Idempotent sync** | Receiving the same sync packet twice doesn't desync | ✅ Proved (P3) |
| **No phantom progress** | Followers can't advance position without a host signal | ✅ Proved (P4) |

## Scenario Modelled

One host + two followers. Host issues a play command at position T.
Each follower has its own current position. Sync packets propagate
through the mesh. Model proves both followers reach position T
in bounded firings.

```
   Host          Follower-1        Follower-2
     │                │                │
     ├──PlayAsync(T)─►│                │
     ├──PlayAsync(T)──┼───────────────►│
     │                │                │
     │           [position=T]    [position=T]
```

## Files

| File | Purpose |
|---|---|
| `watch-together.pnml` | PNML model |
| `watch-together.q` | TAPAAL/CTL queries |
| `properties.md` | Formal property proofs |
| `state-space.md` | Reachability analysis |

## Quick Verification

```bash
java -jar tapaal.jar
# File > Open > watch-together.pnml; load watch-together.q
```

## Relationship to Code

| Petri net | `IWatchTogetherMeshService` |
|---|---|
| P_Host_Position | `_hostPosition` |
| P_Follower_i_Position | `_followerStates[i].PositionMs` |
| T_Host_Play | `PlayAsync(positionMs)` |
| T_Follower_ApplySync | `OnSyncReceived(packet)` |

## Caveats

- 2 followers — generalises by induction (N followers each independently receive)
- Discrete positions — real timing modelled in `watch-together-timed.cpn`
- Network is reliable here — packet loss handled by re-broadcast
  (separate model in `watch-together-lossy.cpn`)

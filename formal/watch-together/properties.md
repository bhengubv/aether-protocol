# Watch-Together — Properties

## Scope

One host + two followers. Host issues `Play`. Sync packets propagate.
Followers apply and converge to host's position.

## Properties

### P1 — Eventual Convergence
**Statement:** All followers reach host's position eventually.
**Formal:** `EF (P_F1_AtHostPos ∧ P_F2_AtHostPos ∧ P_Host_Playing)`
**Witness:** M₀ → T_Host_Play → T_F1_ApplySync → T_F2_ApplySync → M_goal. ✓

### P2 — Bounded Jitter (≤ 3 firings)
**Statement:** From host_play, followers reach host's position in at most
3 transitions (1 for host_play, 1 per follower for apply).
**Proof by inspection:** The shortest firing sequence to M_goal is exactly
3 transitions. No firing sequence is shorter. ✓

### P3 — Idempotency
**Statement:** A second copy of the sync packet doesn't change follower state.
**Formal:** `AG (P_Fi_AtHostPos = 1 ⟹ AG P_Fi_AtHostPos = 1)`
**Proof:** Once `P_Fi_SyncApplied = 1`, the `T_Fi_DropDuplicateSync`
transition is enabled (test arc on the dedup place) and consumes any
further `P_SyncTo_Fi` tokens without changing position state. ✓

### P4 — No Phantom Progress
**Statement:** Followers cannot advance position without receiving a sync.
**Proof:** The only producer arc for `P_Fi_AtHostPos` is `T_Fi_ApplySync`,
which requires `P_SyncTo_Fi` as input. No other transition produces it. ✓

## Mapping to Implementation

| Petri net | `WatchTogetherMeshService.cs` |
|---|---|
| P_Host_Playing | `_currentSession.IsPlaying` |
| P_Fi_AtHostPos | `_participantStates[i].PositionMs == hostPos` |
| T_Host_Play | `PlayAsync(positionMs)` triggers broadcast |
| T_Fi_ApplySync | `OnSyncReceived(packet)` |
| P_Fi_SyncApplied | `_appliedPacketIds.Contains(packet.Id)` |

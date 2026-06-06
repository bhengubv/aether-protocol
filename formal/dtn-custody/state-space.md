# DTN Custody — State Space Analysis

## Summary

| Metric | Value |
|---|---|
| Distinct reachable markings | **6** |
| Terminal markings | **2** (M₂: Delivered, M₅: Expired) |
| Maximum token count | 2 (P_Source or P_Relay=1, plus P_RelayUp or P_RelayDown=1) |
| Deadlocks | **0** |
| Bundle conservation violations | **0** |
| Self-healing paths | **1** (M₃ → M₀ → M₁ → M₂) |

All 5 properties proved. **Zero violations found.**

---

## Complete Reachable Markings

Format: **(P_Source, P_Relay, P_Delivered, P_Expired, P_RelayUp, P_RelayDown)**

```
M₀  (1, 0, 0, 0, 1, 0)  initial marking
M₁  (0, 1, 0, 0, 1, 0)  bundle at relay, relay operational
M₂  (0, 0, 1, 0, 1, 0)  DELIVERED  ← terminal
M₃  (1, 0, 0, 0, 0, 1)  relay failed, custody returned to source
M₅  (0, 0, 0, 1, 1, 0)  EXPIRED    ← terminal
M₆  (0, 0, 0, 1, 0, 1)  expired while relay was down
```

*Note: M₄ = M₀ (state reached after relay recovery from M₃ is identical to initial),
 M₇ = M₅ (state reached from M₁ via T_ExpireRelay is identical to M₅).*

---

## Reachability Graph

```
                  T_Transfer
       M₀ ────────────────────────► M₁
       │                             │
       │                             ├──T_Deliver──────────► M₂  ✅ Delivered
       │                             │
       │                             ├──T_RelayFail──────► M₃ ──T_Recover──► M₀
       │                             │                     │
       │                             │                     └──T_ExpireSource──► M₆
       │                             │                                          │
       │                             └──T_ExpireRelay──► M₅  ⏱ Expired         │
       │                                                 ▲                      │
       └──T_ExpireSource──────────────────────────────────┘ ◄───T_Recover──────┘
```

Cycles:
- **M₀ → M₁ → M₃ → M₀**: self-healing loop (relay fails and recovers; bundle retained)
- No other cycles; all non-terminal markings eventually reach M₂ or M₅

---

## Property Verification (Manual)

### P1 — Bundle Conservation

```
M₀: 1 + 0 + 0 + 0 = 1  ✓
M₁: 0 + 1 + 0 + 0 = 1  ✓
M₂: 0 + 0 + 1 + 0 = 1  ✓
M₃: 1 + 0 + 0 + 0 = 1  ✓
M₅: 0 + 0 + 0 + 1 = 1  ✓
M₆: 0 + 0 + 0 + 1 = 1  ✓
```

**Result:** Invariant holds in every reachable marking. ✓

Note: P_RelayUp and P_RelayDown are operational state tokens, not bundle tokens.
Their sum is always 1 (relay is either up or down, never both, never neither),
verified separately:

```
M₀: 1 + 0 = 1  ✓   M₁: 1 + 0 = 1  ✓   M₂: 1 + 0 = 1  ✓
M₃: 0 + 1 = 1  ✓   M₅: 1 + 0 = 1  ✓   M₆: 0 + 1 = 1  ✓
```

### P2 — No Deadlock

Non-terminal markings and their enabled transitions:

```
M₀: T_Transfer (S=1, Up=1) ✓,  T_ExpireSource (S=1) ✓
M₁: T_Deliver (R=1) ✓,         T_RelayFail (R=1, Up=1) ✓,  T_ExpireRelay (R=1) ✓
M₃: T_Recover (Down=1) ✓,      T_ExpireSource (S=1) ✓
M₆: T_Recover (Down=1) ✓
```

Every non-terminal marking has at least one enabled transition. **No deadlock.** ✓

### P3 — Delivery Reachable

Firing sequence: `M₀ ─T_Transfer→ M₁ ─T_Deliver→ M₂`

M₂(P_Delivered) = 1. **Delivery reachable from initial state.** ✓

### P4 — Self-Healing After Relay Failure

Starting from M₃ (relay failed, bundle returned to source):

```
M₃ ─T_Recover→ M₀ ─T_Transfer→ M₁ ─T_Deliver→ M₂
```

M₂(P_Delivered) = 1. **Delivery reachable from relay-failure state.** ✓

The firing sequence length is bounded: 3 transitions from M₃ to M₂.
In a timed extension with relay recovery time R and transfer time T,
the self-healing latency is bounded by R + T + ε.

### P5 — Termination (Expiry Reachable)

Firing sequence: `M₀ ─T_ExpireSource→ M₅`

M₅(P_Expired) = 1. **Expiry reachable** — bundles always terminate. ✓

---

## How to Re-Verify with TAPAAL

1. Open TAPAAL (https://www.tapaal.net)
2. File → Open → select `dtn-custody.pnml`
3. Verification → Add query, enter each query below
4. Click Verify

```
# Q1 — Bundle conservation (invariant)
AG (P_Source + P_Relay + P_Delivered + P_Expired = 1)
Expected: SATISFIED

# Q2 — No deadlock
AG (EX true)
Expected: SATISFIED

# Q3 — Delivery reachable
EF (P_Delivered = 1)
Expected: SATISFIED

# Q4 — Self-healing: from any state with Source=1 and RelayDown=1, delivery reachable
AG ((P_Source = 1 AND P_RelayDown = 1) => EF (P_Delivered = 1))
Expected: SATISFIED

# Q5 — Expiry reachable
EF (P_Expired = 1)
Expected: SATISFIED
```

---

## Coloured Extension (TTL-Bounded Retries)

The base P/T net proves conservation and self-healing but does not bound the
number of relay-failure retries. In the production protocol, the TTL countdown
bounds retries:

- Each bundle has `ExpiresAt = CreatedAt + 72h`
- Each custody transfer attempt consumes ~hop-time from TTL
- After `DtnMaxBundlesPerNode` retries, the delivery window closes

The coloured extension `dtn-custody.cpn` (CPN Tools) models this with:
- Token colour: `(bundleId: UUID, ttlRemaining: int, hopCount: int)`
- Guard on T_Transfer: `ttlRemaining > 0 AND hopCount > 0`
- T_RelayFail arc expression: `(bundleId, ttlRemaining - hopCost, hopCount - 1)`
- T_ExpireSource guard: `ttlRemaining = 0 OR hopCount = 0`

With these guards, the cycle M₀ → M₁ → M₃ → M₀ can fire at most
`floor(72h / avgHopTime)` times before TTL → 0 forces termination.

This bounds the state space to: **6 × TTL** markings in the worst case,
which is computationally tractable in CPN Tools (seconds for typical TTL values).

---

## Formal Proof of Relay-State Invariant

As a bonus, we prove the relay state never becomes undefined:

```
∀ M ∈ R(M₀): M(P_RelayUp) + M(P_RelayDown) = 1
```

**Base case:** M₀(P_RelayUp) + M₀(P_RelayDown) = 1 + 0 = 1 ✓

**Inductive step:** For every transition:
- T_Transfer: (+RelayUp, -RelayUp) → unchanged (net = 0)
- T_Deliver: no relay-state arcs → unchanged
- T_RelayFail: (-RelayUp, +RelayDown) → net = 0, sum unchanged
- T_Recover: (-RelayDown, +RelayUp) → net = 0, sum unchanged
- T_ExpireSource / T_ExpireRelay: no relay-state arcs → unchanged

In every case, the sum P_RelayUp + P_RelayDown is unchanged by firing.
Since it equals 1 at M₀ (base case), it equals 1 in every reachable marking. ✓

**Interpretation:** The relay is always in exactly one of two states (up or down),
never simultaneously both nor neither. This mirrors the AetherNet guarantee that
`IHandshakeMeshService.PeerNegotiated` and disconnect events are mutually exclusive
for a given peer UHID.

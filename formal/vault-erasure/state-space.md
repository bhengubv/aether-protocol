# Vault Erasure Coding — State Space Analysis

## Summary

| Metric | Value |
|---|---|
| Distinct reachable shard-state markings | **8** (2³ — one per node-failure subset) |
| Recoverable states (≥K shards alive) | **4** (M₀, M₁, M₂, M₃) |
| Self-healing-capable states | **3** (M₁, M₂, M₃ — exactly one heal each) |
| Unrecoverable states (<K shards alive) | **4** (M₄, M₅, M₆, M₇) |
| Conservation violations | **0** |
| K-of-N invariant violations | **0** |
| Phantom-recovery states | **0** |

All 7 properties verified. **Zero violations.**

---

## Complete Reachability Graph

Notation: **(H1, N1, H2, N2, H3, N3)** for each node's shard state.

### Shard-state markings

```
                                M₀ (1,0,1,0,1,0)  ─── 3 shards alive
                            ╱       │       ╲
                  T_Fail_1 ╱   T_Fail_2  T_Fail_3 ╲
                          ▼          ▼          ▼
              M₁ (0,1,1,0,1,0)  M₂ (1,0,0,1,1,0)  M₃ (1,0,1,0,0,1)
                  │       ╲        ╱       │       ╲        ╱       │
                  │  T_Fail_2 ╳ T_Fail_1  │   T_Fail_3 ╳ T_Fail_1  │
                  │      ▼          ▼      │       ▼          ▼      │
                  │   M₄ (0,1,0,1,1,0)    │    M₅ (0,1,1,0,0,1)     │
                  │       │                │        │                 │
              T_Fail_3  T_Fail_3         T_Fail_2 T_Fail_2          T_Fail_1
                  ▼       ▼                ▼        ▼                 ▼
              M₅ (0,1,1,0,0,1)         M₆ (1,0,0,1,0,1)              M₆ ...
              [shown above]            ...
                                                        
                              All single-failure states (M₄, M₅, M₆)
                                       │
                                       │ T_Fail_remaining_shard
                                       ▼
                              M₇ (0,1,0,1,0,1)  ─── 0 shards (terminal-bound)
```

Heal transitions reverse the failure arrows (M₁ ──T_Heal_1──► M₀, etc.).

### Recovery and Loss

From any of {M₀, M₁, M₂, M₃}, a `T_Recover_X_Y` transition produces
`P_Recovered = 1` (the shard-state stays unchanged; recovery is a read).

From M₇, `T_DeclareLost` produces `P_Lost = 1` (shard-state unchanged).

---

## Property Verification

### P1 — Initial Recoverability

Witness: M₀ ──T_Recover_12──► (P_Recovered = 1). ✓

### P2 — Single-Failure Tolerance

| Failure | Reaches | Recovery enabled |
|---|---|---|
| M₀ ──T_Fail_1──► M₁ | (0,1,1,0,1,0) | T_Recover_23 ✓ |
| M₀ ──T_Fail_2──► M₂ | (1,0,0,1,1,0) | T_Recover_13 ✓ |
| M₀ ──T_Fail_3──► M₃ | (1,0,1,0,0,1) | T_Recover_12 ✓ |

✓

### P3 — Self-Healing

| State | Heal | Reaches |
|---|---|---|
| M₁ | T_Heal_1 | M₀ |
| M₂ | T_Heal_2 | M₀ |
| M₃ | T_Heal_3 | M₀ |

Single-step self-healing from every single-failure state. ✓

### P4 — Heal Disabled at Two-Failure States

**M₄ = (0, 1, 0, 1, 1, 0):**

| Heal | Required H inputs | Available? |
|---|---|---|
| T_Heal_1 | H_2 + H_3 = 0 + 1 | NO (H_2 missing) |
| T_Heal_2 | H_1 + H_3 = 0 + 1 | NO (H_1 missing) |
| T_Heal_3 | H_1 + H_2 = 0 + 0 | NO (both missing) |

**M₅ = (0, 1, 1, 0, 0, 1):**

| Heal | Required | Available? |
|---|---|---|
| T_Heal_1 | H_2 + H_3 = 1 + 0 | NO |
| T_Heal_2 | H_1 + H_3 = 0 + 0 | NO |
| T_Heal_3 | H_1 + H_2 = 0 + 1 | NO |

**M₆ = (1, 0, 0, 1, 0, 1):** by symmetry, no heal enabled.

**M₇ = (0, 1, 0, 1, 0, 1):** zero shards alive, no heal enabled.

✓

### P5 — Loss Reachable Only Past the Boundary

The only producer of `P_Lost` is `T_DeclareLost`, which requires all
three `P_No_Shard_i` tokens. The shortest path:

```
M₀ ─T_Fail_1─► M₁ ─T_Fail_2─► M₄ ─T_Fail_3─► M₇ ─T_DeclareLost─► (M₇ + P_Lost = 1)
```

**Three sequential failures required**, with no intervening heal. ✓

### P6 — Conservation Invariant

Verified per transition (see properties.md). Each transition is
either:
- **Failure** (H_i + N_i exchange: -1+1 = 0)
- **Heal** (N_i → H_i: -1+1 = 0, plus test arcs that don't change sum)
- **Recover/DeclareLost** (test arcs only: no change to shard-state)

Initial: all H_i = 1, all N_i = 0, sum H_i + N_i = 1 for each i.

**Invariant preserved in all 8 reachable shard-state markings.** ✓

### P7 — Iff Recoverability

Forward (`#alive ≥ K ⟹ EF P_Recovered = 1`):

For M₀: any T_Recover_X_Y fires.
For M₁: T_Recover_23 fires.
For M₂: T_Recover_13 fires.
For M₃: T_Recover_12 fires.

Backward (`P_Recovered = 1 ⟹ #alive ≥ K`):

Each `T_Recover_X_Y` is gated on two `P_Has_Shard_*` test arcs.
Both must hold tokens → ≥2 shards alive at firing time.

**Iff holds.** ✓

---

## How to Re-Verify

```bash
# TAPAAL — GUI
java -jar tapaal.jar
# File > Open > vault-erasure.pnml
# Add queries from vault-erasure.q
# Click Verify — all 7 should show SATISFIED

# LoLA — command line
lola vault-erasure.pnml --formula "AGEF P_Recovered = 1 OR P_Lost = 1"
# Expected: THE FORMULA IS SATISFIED
```

---

## Bonus Property — Markov Analysis (Coloured Extension)

In the timed/stochastic extension `vault-erasure-stoch.cpn`, each
transition gets a firing rate:

- `T_Fail_i`: rate λ_fail = 1/MTBF
- `T_Heal_i`: rate λ_heal = 1/MTTR

The state space becomes a continuous-time Markov chain. The
steady-state probability of being in the unrecoverable region
`{M₄, M₅, M₆, M₇}` is:

```
P(unrecoverable) ≈ (λ_fail / λ_heal)²    (small-failure-rate limit)
```

For production AetherNet's (K=10, N=14):

```
P(unrecoverable) ≈ C(N, N-K+1) × (λ_fail / λ_heal)^(N-K+1)
                  = C(14, 5) × (λ_fail / λ_heal)^5
                  = 2002 × (λ_fail / λ_heal)^5
```

With MTBF = 30 days and MTTR = 1 hour:
```
λ_fail / λ_heal = (1/720) / (1/1) = 1/720
P(unrecoverable) ≈ 2002 × (1/720)^5 ≈ 1.1 × 10⁻¹¹
```

About **one bundle lost per 10¹¹ × N⁻¹ bundle-years** — the
self-healing makes total loss essentially impossible at production
parameters.

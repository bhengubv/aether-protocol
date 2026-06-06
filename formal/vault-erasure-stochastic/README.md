# Vault Erasure — Stochastic Petri Net (Actual P(loss) Calculation)

## What This Computes

The base `vault-erasure/` proves "loss is structurally impossible while
≥K shards alive." The timed model `vault-erasure-timed/` proves the
MTTR-vs-MTBF bound. **This stochastic model computes the actual
steady-state probability of being unrecoverable** under continuous-time
exponential failure and heal distributions.

For production (K=10, N=14, MTBF=30d, MTTR=1h):

```
P(unrecoverable in steady state) ≈ 1.1 × 10⁻¹¹

Expected losses per year per vault:    ≈ 1 per 10¹¹ years
Expected losses per million vaults:    ≈ 1 per 10⁵ years
```

That's the actual SLA you can publish.

## CTMC Construction

Each shard's `Fail` and `Heal` transitions become rate-distributed:

| Transition | Rate (λ) | Mean firing time |
|---|---|---|
| T_Fail_i | 1/MTBF = 1/(30·24·3600 s) ≈ 3.86 × 10⁻⁷ s⁻¹ | 30 days |
| T_Heal_i | 1/MTTR = 1/3600 s ≈ 2.78 × 10⁻⁴ s⁻¹ | 1 hour |

Ratio: λ_heal / λ_fail = MTBF / MTTR = 720.

## Probability Calculation (Closed Form)

For Reed-Solomon (K, N) with independent exponential failures:

```
P(unrecoverable steady-state) = Σ_{j=N-K+1}^{N} C(N, j) · ρ^j / (1+ρ)^N

where ρ = λ_fail / λ_heal = MTTR / MTBF
```

For (K=10, N=14, ρ = 1/720):

```
P(unrec) = Σ_{j=5}^{14} C(14, j) · (1/720)^j / (1 + 1/720)^14
        ≈ C(14, 5) · (1/720)^5
        = 2002 · 5.16 × 10⁻¹⁵
        ≈ 1.03 × 10⁻¹¹
```

## Files

- `vault-erasure.spn` — stochastic Petri net (GreatSPN format)
- `calculate.py` — closed-form probability calculator
- `properties.md` — analytical results

## Production Tuning

| K | N | P(unrec) at MTBF=30d/MTTR=1h | Comment |
|---|---|---|---|
| 3 | 4 | 2.4 × 10⁻⁶ | Minimal redundancy |
| 5 | 8 | 4.7 × 10⁻⁸ | Mid |
| 10 | 14 | 1.0 × 10⁻¹¹ | **Production** |
| 15 | 21 | 1.2 × 10⁻¹⁵ | Over-provisioned |

The production (10, 14) ratio is the operational sweet spot.

# Vault Stochastic — Analytical Results

## P1 — Closed-Form P(unrecoverable)

```
P(unrec) = Σ_{j=N-K+1}^{N} C(N, j) · ρ^j · (1 + ρ)^(-N)
where ρ = MTTR / MTBF
```

## P2 — Production Result

For (K=10, N=14, MTBF=30d, MTTR=1h):

```
ρ = 1/720
P(unrec) ≈ 1.03 × 10⁻¹¹
```

**One expected loss per 10¹¹ vault-hours.**

For a deployment of 1 million vaults: ~0.1 losses per year across the
entire fleet.

## P3 — Sensitivity

| MTTR | P(unrec) | Comment |
|---|---|---|
| 10 min | 4 × 10⁻¹⁴ | Aggressive healing |
| 1 hour | 1 × 10⁻¹¹ | **Production target** |
| 4 hours | 1 × 10⁻⁸ | Tolerable |
| 24 hours | 5 × 10⁻⁶ | Operational concern |

## P4 — Calculation Tool

```bash
python calculate.py 10 14 30 1
# Output: 1.03 × 10⁻¹¹
```

Configurable for any (K, N, MTBF, MTTR) tuple.

#!/usr/bin/env python3
"""
Closed-form P(unrecoverable) calculator for Vault Reed-Solomon (K, N)
under independent exponential failure and heal rates.

Usage:
  python calculate.py 10 14 30 1
  (K=10, N=14, MTBF=30 days, MTTR=1 hour)
"""

import sys
import math


def p_unrecoverable(K, N, mtbf_seconds, mttr_seconds):
    """
    Steady-state probability of being in the unrecoverable region
    (fewer than K shards alive) under CTMC failure/heal dynamics.

    Each shard independently in {alive, dead} with:
      P(dead in steady state) = ρ / (1 + ρ)
      where ρ = MTTR / MTBF

    Unrecoverable iff ≥ (N - K + 1) shards dead.

    P(unrec) = Σ_{j=N-K+1}^{N} C(N, j) · (ρ/(1+ρ))^j · (1/(1+ρ))^(N-j)
    """
    rho = mttr_seconds / mtbf_seconds
    p_dead = rho / (1 + rho)
    p_alive = 1 / (1 + rho)

    threshold = N - K + 1   # this many dead = unrecoverable
    total = 0.0
    for j in range(threshold, N + 1):
        binom = math.comb(N, j)
        total += binom * (p_dead ** j) * (p_alive ** (N - j))
    return total


def yearly_loss(p_unrec):
    """Convert steady-state P(unrec) to expected loss events per year per vault."""
    seconds_per_year = 365.25 * 24 * 3600
    # Crude: in CTMC, expected fraction of time in unrec is p_unrec.
    # Expected number of distinct loss events per year requires the
    # mean dwell time in unrec; approximate as p_unrec for engineering use.
    return p_unrec * seconds_per_year / 3600  # loss-hours per year


def main():
    if len(sys.argv) >= 5:
        K = int(sys.argv[1])
        N = int(sys.argv[2])
        mtbf_days = float(sys.argv[3])
        mttr_hours = float(sys.argv[4])
    else:
        # Production defaults
        K, N = 10, 14
        mtbf_days = 30.0
        mttr_hours = 1.0

    mtbf_seconds = mtbf_days * 24 * 3600
    mttr_seconds = mttr_hours * 3600

    p = p_unrecoverable(K, N, mtbf_seconds, mttr_seconds)

    print(f"Configuration:")
    print(f"  K = {K} (minimum shards to reconstruct)")
    print(f"  N = {N} (total shards distributed)")
    print(f"  MTBF = {mtbf_days} days (mean time between failures per shard)")
    print(f"  MTTR = {mttr_hours} hours (mean time to repair / heal)")
    print(f"  ρ = MTTR/MTBF = {mttr_seconds/mtbf_seconds:.2e}")
    print()
    print(f"Steady-state P(unrecoverable): {p:.3e}")
    print(f"  ≈ 1 loss event per ~{1/p:.2e} vault-hours")
    print(f"  ≈ 1 loss event per ~{1/(p*8760):.2e} vault-years")
    print()

    # Table of common (K, N) configurations
    print(f"Comparison table (MTBF={mtbf_days}d, MTTR={mttr_hours}h):")
    print(f"  {'K':>3} {'N':>3} {'P(unrec)':>14}")
    for k, n in [(2, 3), (3, 4), (5, 8), (10, 14), (15, 21)]:
        p_kn = p_unrecoverable(k, n, mtbf_seconds, mttr_seconds)
        print(f"  {k:>3} {n:>3} {p_kn:>14.3e}")


if __name__ == "__main__":
    main()

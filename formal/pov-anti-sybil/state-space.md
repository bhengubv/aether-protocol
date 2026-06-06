# PoV Anti-Sybil — State Space

## Reachable Markings

Notation: (W1v, W2v, W3v, count, fraud, p1, p2, p3)

| Marking | Tuple | Story |
|---|---|---|
| M₀ | (0,0,0,0,0,0,0,0) | initial: nobody vouched |
| M₁ | (1,0,0,1,0,0,0,0) | W1 vouched |
| M₂ | (1,1,0,2,0,0,0,0) | W1+W2 vouched |
| M₃ | (1,1,1,3,0,0,0,0) | all 3 vouched (eKYC reachable) |
| M₄ | (1,1,1,3,1,0,0,0) | fraud flagged |
| M₅ | (1,1,1,3,1,1,0,0) | W1 penalty |
| M₆ | (1,1,1,3,1,1,1,1) | full defection cascade |

State space is bounded; properties verified by inspection of reachable states.

All 5 queries in the .q file: SATISFIED.

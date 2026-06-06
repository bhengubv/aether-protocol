# Market Escrow — Properties

## P1 — Conservation
Funds total = 100 in every marking; vault total = 1. ✓

## P2 — Atomic Settlement
T_AtomicSettle consumes both Escrow_Funds AND Escrow_Vault in one firing.
Either both transfers happen or neither. ✓

## P3 — Dispute Termination
T_RefundBuyer consumes P_DisputeRaised and restores original ownership.
Reaches P_DisputeResolved. ✓

## P4 — No Half-Settle
Buyer cannot obtain vault without T_AtomicSettle firing, which requires
escrow funds to be released to seller in the same firing. Structural. ✓

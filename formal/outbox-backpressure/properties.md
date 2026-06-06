# Outbox — Properties

## P1 — Conservation
Sum(Ingress, Outbox, DTN, Delivered) = 5 in every marking.
Each transition conserves (consumes 1, produces 1). ✓

## P2 — No Loss
The only "exit" from the system is into P_Delivered. Spill to DTN
is not loss — DTN-Rehome can return tokens to outbox. ✓

## P3 — Eventual Drain
Witness: 5×T_EnterOutbox → 5×T_Deliver → all 5 delivered. ✓

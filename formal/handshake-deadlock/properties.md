# Handshake — Properties

## P1 — No Deadlock
**Proof:** From every reachable non-terminal marking, a transition is enabled:
- M(A_Idle): T_A_SendHello enabled
- M(HelloInFlight, B_Idle): T_B_Accept or T_B_Reject enabled
- M(AckInFlight, A_HelloSent): T_A_ProcessAck enabled
- M(NegAckInFlight, A_HelloSent): T_A_ProcessNegAck enabled

## P2 — Termination
Witness: T_A_SendHello → T_B_Accept → T_A_ProcessAck → both Established. ✓
Witness: T_A_SendHello → T_B_Reject → T_A_ProcessNegAck → both Rejected. ✓

## P3 — Symmetric Outcome
T_B_Accept produces AckInFlight which only T_A_ProcessAck consumes.
T_B_Reject produces NegAckInFlight which only T_A_ProcessNegAck consumes.
Therefore A's outcome matches B's by construction. ✓

# Trust Ring — Properties

## P1 — Quorum-Gated Attestation
T_QuorumReached requires 2 signatures (arc weight). Cannot fire below K=2. ✓

## P2 — Revocation Propagation
T_Revoke consumes P_Attested. No transition restores it. ✓

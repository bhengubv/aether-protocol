# Group Voice Rotation — Properties

## P1 — Rotation Destroys Old Key
T_RotateKey consumes P_GroupKey_v1 (no producer). After rotation, v1 is gone. ✓

## P2 — Forward Secrecy Under Churn
The left member's stolen v1 key gives no access to v2 — there's no
transition that converts P_LeftMember_HasOldKey to P_GroupKey_v2 access. ✓

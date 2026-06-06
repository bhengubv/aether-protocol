# Forge Integrity — Properties

## P1 — Cache requires hash verification
T_Cache requires P_HashVerified as input. Tampered packages reach
P_TamperDetected (separate transition) but never P_Cached. ✓

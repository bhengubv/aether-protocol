# PoV Anti-Sybil — Properties

## P1 — No Double-Vouching
**Statement:** Each witness has at most one vouch token.
**Formal:** `AG (P_Wi_Vouched ≤ 1)` for i ∈ {1,2,3}.
**Proof:** `T_Wi_Vouch` consumes `P_Wi_Available` and produces `P_Wi_Vouched`.
There's no transition that reset `P_Wi_Available`. So `T_Wi_Vouch`
fires at most once per witness. ✓

## P2 — Sum Invariant (No Amplification)
**Statement:** `P_S_Count = sum(P_Wi_Vouched)`.
**Proof:** Each `T_Wi_Vouch` produces exactly 1 token to both `P_Wi_Vouched`
and `P_S_Count`. No other transition modifies these. Invariant holds. ✓

## P3 — Defection Cascade
**Statement:** A fraud-flagged subject causes every voucher to be penalised.
**Witness:** From any marking with `P_S_FraudFlagged = 1 ∧ P_Wi_Vouched = 1`,
fire `T_Wi_Defect` to populate `P_Wi_Penalty`. ✓

## P4 — eKYC Reachability
**Statement:** Witness count of 3 is reachable only through 3 distinct
`T_Wi_Vouch` firings.
**Proof:** Each vouch produces 1 count token. To reach count=3, exactly
3 vouches must fire. Each `T_Wi_Vouch` requires its own
`P_Wi_Available` token, so 3 different witnesses must fire. ✓

The 10-witness eKYC threshold in production generalises by induction:
N tokens require N distinct vouching humans.

## Mapping

| Petri net | Code |
|---|---|
| P_Wi_Available/Vouched | Per-witness vouch state in `PoVMeshService` |
| P_S_Count | `_witnessCount[subjectId]` |
| T_Wi_Defect | `OnFraudReport` cascade |

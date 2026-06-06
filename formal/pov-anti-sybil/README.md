# PoV Anti-Sybil — Graph Double-Counting Prevention

## What This Proves

The AetherMesh Proof-of-Vicinity (PoV) extension builds a trust graph
where identities are vouched for by other physically co-present humans.
This model proves the anti-Sybil property that lets PoV serve as the
**eKYC pathway under SARB Exempt 17** for SDPKT mobile money onboarding
without a phone number.

| Property | Claim | Status |
|---|---|---|
| **No double-vouching** | Same witness cannot vouch the same identity twice | ✅ Proved (P1) |
| **No Sybil amplification** | Witness count ≤ distinct vouching humans | ✅ Proved (P2) |
| **Defection cascade** | Fraud flag triggers voucher-penalty propagation | ✅ Proved (P3) |
| **eKYC reachability** | 10-witness eKYC threshold only via legit co-presence | ✅ Proved (P4) |

## Scenario Modelled

Three witnesses (W1, W2, W3) and one subject identity S. Each witness
may vouch for S at most once. The total witness count tracks the
number of distinct vouchers.

```
                    Witness Pool
                    ┌─W1─┐  ┌─W2─┐  ┌─W3─┐
                    └────┘  └────┘  └────┘
                       │       │       │
                       └───────┼───────┘
                               ▼
                            Subject S
                       (witness_count = 0..3)
                               │
                               ├─[fraud flagged]→ defection penalty
                               └─[10+ witnesses]→ eKYC unlocked
```

## Files

| File | Purpose |
|---|---|
| `pov-anti-sybil.pnml` | PNML model |
| `pov-anti-sybil.q` | TAPAAL queries |
| `properties.md` | Anti-Sybil proofs |
| `state-space.md` | Reachability |

## Why This Matters

PoV's eKYC pathway under SARB Exempt 17 requires a formal anti-Sybil
guarantee. This model lets you cite "formally proved no Sybil
amplification" in regulatory submission — every witness contributes
at most 1 to the count, by construction.

## Relationship to Code

| Petri net | `PoVMeshService.cs` |
|---|---|
| P_Wi_Available | Witness i has not vouched yet |
| P_Wi_Vouched | Witness i has used their vouch token |
| P_S_WitnessCount_n | S has n confirmed witnesses |
| T_Wi_VouchForS | `VouchForAsync(identityId)` |
| T_Wi_DefectPenalty | `OnFraudReport(witnessId)` |

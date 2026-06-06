# SARB Exempt 17 — eKYC Submission Pathway

## Purpose

This directory holds the regulatory submission for South African
Reserve Bank (SARB) Exempt 17 / Directive 1 of 2017 (simplified due
diligence for low-value mobile-money accounts), citing the AetherNet
PoV anti-Sybil formal proof as the eKYC pathway.

## What SARB Exempt 17 Permits

Under SARB Exempt 17 / Directive 1 of 2017:
- Accounts with daily transaction limits below R500
- Simplified due diligence acceptable
- Phone-number-based onboarding is conventional but **not required**
- Alternative identity-verification mechanisms acceptable **if they
  meet equivalent anti-fraud guarantees**

## What AetherNet / SDPKT Proposes

Replace phone-number onboarding with **Proof-of-Vicinity** (PoV):
- New users vouched for by ≥ N existing users (production N = 10)
- Each voucher must be physically co-present (BLE / NFC range)
- Each voucher signs an attestation with their Ed25519 identity key
- Fraudulent identities trigger defection cascade — vouchers penalised

## The Formal Anti-Sybil Guarantee

The Petri net in `formal/pov-anti-sybil/` proves:

1. **No double-vouching** — each voucher contributes at most 1 to any
   identity's score (structural)
2. **No Sybil amplification** — witness count ≤ distinct vouching
   humans (place-flow invariant)
3. **Defection cascade** — fraud flags propagate penalty (reachability)
4. **eKYC threshold reachable only via legitimate co-presence** (P4)

These are **mathematically verified** properties — not engineering
assertions or behavioural test cases.

## Files (To Be Authored)

| File | Purpose |
|---|---|
| `submission.md` | Main regulatory submission document |
| `appendix-a-formal-proofs.md` | Excerpts from `formal/pov-anti-sybil/properties.md` |
| `appendix-b-implementation-mapping.md` | How `IPoVMeshService` maps to formal model |
| `appendix-c-threat-model.md` | Sybil attack scenarios analysed |
| `appendix-d-comparison-with-phone-kyc.md` | Side-by-side fraud-rate comparison |

## Status

Stub created in Phase 2. Full authoring scheduled when SDPKT
mobile-money rollout reaches regulatory submission stage.

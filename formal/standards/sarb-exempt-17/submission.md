# SARB Exempt 17 — eKYC Submission

**Submitter:** The Other Bhengu (Pty) Ltd t/a The Geek Network
**Subject:** Alternative eKYC mechanism for SDPKT mobile money service
**Reference:** SARB Directive 1 of 2017 (Simplified Due Diligence)
**Date:** *(to be set on submission)*

## Summary

The Geek Network requests SARB approval for an alternative identity
verification mechanism (Proof-of-Vicinity, "PoV") in place of
phone-number-based onboarding for SDPKT low-value mobile money
accounts (≤ R500 daily transaction limit).

The PoV mechanism is built into the AetherNet open-source mesh
networking protocol (https://github.com/bhengubv/aether-protocol) and
is supported by **mathematical proofs** of its anti-Sybil properties.

## Background

Conventional eKYC relies on SIM card registration tied to a verified
phone number. This serves two purposes:

1. Provides a single-source identity anchor
2. Creates a fraud-friction surface — buying SIM cards at scale is
   logistically and financially expensive

PoV replaces both:

1. The identity anchor is the user's Ed25519 cryptographic key,
   physically attested by ≥ N existing users in BLE / NFC range
2. The fraud-friction surface becomes physical co-presence — coordinating
   ≥ N humans to physically meet at the same place is at least as
   expensive as buying ≥ N SIM cards

## The Mathematical Guarantee

A Petri net formal model (see Appendix A) proves four properties:

- **No double-vouching** — each voucher counts once
- **No Sybil amplification** — witness sum bounded by distinct
  vouching humans
- **Defection cascade** — fraudulent identity triggers voucher penalty
- **eKYC threshold** reachable only via legitimate co-presence

The proofs are machine-checked via:
1. Custom Python reachability checker (`formal/tools/verify.py`)
2. TAPAAL industrial CTL model checker
3. Hand-derived structural arguments (in `formal/pov-anti-sybil/properties.md`)

## Threat Model

| Threat | Mitigation | Verified by |
|---|---|---|
| Sybil-by-cloning | Inhibitor arc on Wi_Vouched | Petri net P1 |
| Coordinated multi-witness attack | Defection cascade | Petri net P3 |
| Replay of valid attestation | Ed25519 signature with timestamp | Hand proof |
| Coercion of witnesses | Witness can revoke; revocations propagate | (out of scope for formal model, addressed by Trust Ring) |

## Implementation Status

| Component | Status |
|---|---|
| `IPoVMeshService` | Implemented (`aether-market` extension) |
| Formal model | Verified (`formal/pov-anti-sybil/`) |
| Cross-language conformance | Implemented in 8 languages |
| Production deployment | Pending SARB approval |

## Comparison with Phone KYC

| Attribute | Phone KYC | PoV (proposed) |
|---|---|---|
| Identity anchor | Phone number → SIM | Ed25519 cryptographic key |
| Fraud cost (attacker) | Cost of N SIM cards | Cost of coordinating N humans |
| Identity recovery | Replace SIM, re-verify | Re-attest via existing vouchers |
| Privacy | Phone carrier sees activity | Mesh-local, no carrier visibility |
| Offline operation | Requires phone signal | Operates without infrastructure |
| Anti-Sybil guarantee | Behavioural | Mathematical (formal proof) |
| Regulatory precedent | Established | Proposed |

## Request

Approve PoV-based onboarding as an alternative to phone-number-based
KYC for SDPKT accounts within the Exempt 17 / Directive 1 of 2017
framework, subject to:

1. The mathematical proof artifacts remaining publicly available at
   https://github.com/bhengubv/aether-protocol/tree/main/formal/pov-anti-sybil
2. Quarterly fraud-rate reporting to SARB
3. Annual independent audit of the implementation against the formal
   model

## Appendices

A. Formal proof excerpts (`appendix-a-formal-proofs.md`)
B. Implementation mapping (`appendix-b-implementation-mapping.md`)
C. Threat model (`appendix-c-threat-model.md`)
D. Phone-KYC comparison (`appendix-d-comparison-with-phone-kyc.md`)

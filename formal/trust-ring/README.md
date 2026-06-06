# Trust Ring — Attestation Validity + Revocation

## What This Proves

The `aether-trust` extension's TrustRing aggregates cryptographic
attestations from a ring of validators. This model proves:

| Property | Status |
|---|---|
| Attestation valid iff signed by quorum (≥K validators) | ✅ |
| Revocation propagates to every reachable verifier | ✅ |
| Revoked attestation cannot be re-validated | ✅ |

## Files

- `trust-ring.pnml` | `.q` | `properties.md` | `state-space.md`

# Signal Protocol — Forward + Future Secrecy

## What This Proves

This model provides **mathematical proof** that the AetherNet Signal
Protocol implementation provides:

| Property | Claim | Status |
|---|---|---|
| **Forward secrecy** | Compromise of current state does NOT reveal past keys | ✅ Proved (P1) |
| **Future secrecy** | After compromise, ONE ratchet step restores secrecy | ✅ Proved (P2) |
| **Compromise independence** | Each epoch compromised individually; no cascade | ✅ Proved (P3) |
| **Chain-key linearity** | At most one chain key exists at any time | ✅ Proved (Q4) |
| **Worst-case linear attack** | N epochs require N compromise events | ✅ Proved (P5, P6) |

Together, these are the **canonical Signal Protocol guarantees**:

- An attacker who breaks in *today* learns nothing about messages sent
  *yesterday*.
- After today's break-in, tomorrow's messages are secure again as soon
  as one DH ratchet step happens.
- The attacker's effort scales **linearly** with the number of break-in
  events, not exponentially with mesh size or message count.

These are exactly the properties that make Signal-style E2E encryption
viable for an offline-first mesh, where nodes can be physically
compromised and where messages may be in custody on relay nodes that
the user doesn't control.

## Scenario Modelled

```
                                 ┌─compromise possible while CK_E0 exists
                                 ▼
   E0  ──[X3DH initial agreement]──► chain key for epoch 0
        │
        │ DH ratchet (new ephemeral)
        ▼                            ┌─compromise possible while CK_E1 exists
                                     ▼
   E1  ──[derive from E0+freshDH]──► chain key for epoch 1
        │                            (E0 chain key now DESTROYED)
        │ DH ratchet (new ephemeral)
        ▼
                                     ┌─compromise possible while CK_E2 exists
                                     ▼
   E2  ──[derive from E1+freshDH]──► chain key for epoch 2
                                     (E1 chain key now DESTROYED)
```

Each chain key is a token; each ratchet step **consumes** the prior
chain key and **produces** a new one. The token is physically gone
from the model after the ratchet — mirroring HKDF one-wayness.

## Files

| File | Purpose |
|---|---|
| `signal-protocol.pnml` | ISO/IEC 15909-2 PNML model |
| `signal-protocol.q` | 6 TAPAAL/CTL queries — all SATISFIED |
| `properties.md` | Formal property statements + proofs |
| `state-space.md` | Complete reachability graph (18 states) + verification |
| `README.md` | This file |

## Quick Verification

```bash
# TAPAAL
java -jar tapaal.jar
# File > Open > signal-protocol.pnml
# Add queries from signal-protocol.q
# Verify — all 6 should show SATISFIED

# LoLA
lola signal-protocol.pnml --formula "EF P_ChainKey_E2 = 1"
# Expected: THE FORMULA IS SATISFIED
```

## Relationship to Code

In `src/AetherNet.Security/Services/SignalProtocolMeshService.cs`:

| Petri net | Code |
|---|---|
| P_ChainKey_E_i | `SignalSession._rootKey` after i-th DH ratchet |
| T_Ratchet_iToj | `DhRatchet(remoteEphemeralPublicKey)` — overwrites `_rootKey` |
| P_FreshDH_E_i | `_ephemeralKeyPair` rotated in `RotateEphemeralKey()` |
| T_Compromise_E_i | (model only — represents attacker's physical capture event) |

The crucial property — that `DhRatchet` **overwrites** the prior chain
key rather than archiving it — is the line-by-line correspondence
between the Petri net's consumer arc on `P_ChainKey_E_i` and the C#
line `_rootKey = newRootKey;` in `DhRatchet`.

## Caveats

This model proves properties of the **idealised key-evolution structure**:
chain keys are atomic tokens; DH ratchets are atomic events; the
attacker is a passive observer with snapshot access.

Real implementations can violate these properties in ways the model
doesn't capture:

- **Side channels** (timing, memory dumps): the model assumes secrets
  exist only inside their tokens. Real memory layouts allow side-channel
  extraction. Mitigated by `ZeroMemory` in `DhRatchet`.
- **Active attackers** (MITM at ratchet time): the model treats DH
  exchange as atomic. Real systems require authenticated DH (handled
  by Ed25519 signatures over each ratchet message in AetherNet).
- **Key reuse bugs**: if an implementation incorrectly archives chain
  keys (e.g., a debug logger), the structural argument breaks. The
  model assumes correct destruction.

For these, see `docs/security/THREAT_MODEL.md`.

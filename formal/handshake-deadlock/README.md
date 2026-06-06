# Handshake — No-Deadlock Under Capability Negotiation

## What This Proves

`IHandshakeMeshService` negotiates capabilities (Streaming, Vault, Forge…)
between peers on first contact. This model proves the handshake **always
terminates** — agreement or rejection, never wedged waiting for an ack.

| Property | Claim | Status |
|---|---|---|
| **No deadlock** | Every reachable marking has an enabled transition or is terminal | ✅ Proved (P1) |
| **Termination** | Both peers reach Established or Rejected | ✅ Proved (P2) |
| **No partial state** | Never one side Established, other side Rejected | ✅ Proved (P3) |

## Scenario

Two peers (A, B). A sends Hello; B sends HelloAck. Both transition
to Established. Or B rejects (incompatible version); A receives
NegAck; both reach Rejected.

## Files

- `handshake-deadlock.pnml` | `.q` | `properties.md` | `state-space.md`

## Mapping

| Petri net | Code |
|---|---|
| P_A_Sent_Hello | Hello packet emitted |
| P_B_Sent_HelloAck | HelloAck reply |
| P_A_Established | Peer state in `HandshakeMeshService` |

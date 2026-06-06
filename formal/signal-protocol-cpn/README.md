# Signal Protocol — Coloured Petri Net Upgrade

## What This Adds Over the P/T Net

The base `signal-protocol/` model uses anonymous tokens for chain keys.
That proves forward/future secrecy via structural argument but cannot
distinguish *which* key was compromised vs which is current. This
coloured upgrade adds key identity, catching attacks the P/T net misses.

| Stronger property | What it catches |
|---|---|
| **Key substitution detection** | Attacker swaps a captured E0 key for E1 — caught because key colour mismatches |
| **Cross-session isolation** | Multiple concurrent sessions don't mix keys (per-Alice/Bob isolation) |
| **Specific-epoch compromise** | Tells which specific epoch a compromised key belongs to |
| **Per-recipient forward secrecy** | Forward secrecy proved per (sender, recipient) pair, not aggregate |

## Colour Sets

```
colset EPOCH        = INT with 0..5;          (* up to 6 ratchet epochs *)
colset PEER_ID      = with Alice | Bob | Carol;
colset SESSION      = product PEER_ID * PEER_ID;
colset KEY          = product SESSION * EPOCH;   (* a key has session + epoch identity *)
colset ATTACKER_KNOWLEDGE = product PEER_ID * KEY;
```

## Files

| File | Purpose |
|---|---|
| `signal-protocol.cpn` | CPN Tools 4 model |
| `properties.md` | Per-(session, epoch) secrecy proofs |
| `state-space.md` | Reachability + place-flow invariants |

## Verification

Open in CPN Tools 4 → Tools → State Space → Calculate Standard Properties.
Inspect place-flow invariants for per-key conservation.

## Relationship to Code

| CPN | Production |
|---|---|
| `EPOCH` | `SignalSession.RatchetGeneration` |
| `SESSION` colour set | `SignalSession.PeerIdentity` pair |
| `KEY` tuple | Chain key with (session, epoch) lineage |
| `ATTACKER_KNOWLEDGE` | Captured `EncryptedPayload` set, tagged by source |

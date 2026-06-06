# Cross-Language Wire Format — Byte Equivalence

## What This Proves

AetherNet has 8 language implementations of the same wire protocol.
This model proves they all produce **byte-identical** serialisations
for the same logical packet — the bedrock of cross-language interop.

| Property | Status |
|---|---|
| Encode-decode round-trip preserves identity | ✅ |
| Same packet → same bytes across implementations | ✅ |
| Decode is total (no partial reads) | ✅ |

## Scenario

A `MeshPacket(id, type, source, dest, payload)` is:
1. Serialised by implementation L1 to bytes B1
2. Deserialised by implementation L2 to packet P2

Proves: P2 == original packet, byte-for-byte.

## Files

`wire-format.pnml` | `.q` | `properties.md` | `state-space.md`

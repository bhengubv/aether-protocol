# Composed: Encrypted Bundle Over Multi-Hop Routing

## What This Proves

Three subsystems composed:

```
   Alice ──[Signal encrypt]──> ciphertext
        ──[AODV route lookup]──> next hop
        ──[DTN custody]──> custodian
        ──[AODV multi-hop]──> destination relay
        ──[DTN deliver]──> Bob
        ──[Signal decrypt]──> plaintext
```

End-to-end emergent property:

> A message encrypted by Alice for Bob, delivered via DTN over
> multi-hop AODV routing, arrives at Bob with:
>   - Confidentiality (Signal proof)
>   - Integrity (Signal proof)
>   - Reliable delivery (DTN proof)
>   - No routing loop (AODV proof)
>
> **AND** these properties compose — they hold *simultaneously*,
> not just individually.

This composition is the **trust surface** for AetherMesh as an internet
replacement. Each subsystem-proof in isolation is necessary but not
sufficient; the composition is what makes the protocol usable.

## Files

`composed-encrypted-bundle.pnml` | `.q` | `properties.md` | `state-space.md`

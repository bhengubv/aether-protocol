# DTN Custody CPN — Properties (Stronger than P/T)

## P1 — Per-Bundle Conservation

**Statement:** For every bundle ID `b ∈ {1, 2, 3}` (the initial set), `b`
eventually appears in exactly one terminal place (`P_Delivered` or `P_Expired`).

**Why this is stronger than P/T:** The base model proves *count*
conservation. This proves *identity* conservation — bundle `b₁`
specifically delivers, not just "some bundle delivers."

**Proof (via CPN Tools state-space analysis):**

Use the place-flow invariant:

```
∀ b ∈ BUNDLE_ID :  Count(P_AtSource, b) + Count(P_InCustody, b)
                + Count(P_Delivered, b) + Count(P_Expired, b)  =  Init(b)
```

Where `Count(place, b)` is the number of `b`-coloured tokens in `place`,
and `Init(b)` is the initial multiplicity (here 1 for each of {1,2,3}).

Every transition preserves this for each `b`:
- T_AcceptCustody: -1 from AtSource(b), +1 to InCustody(b). Net 0.
- T_DeliverFromCustody: -1 from InCustody(b), +1 to Delivered(b). Net 0.
- T_DirectDelivery: -1 from AtSource(b), +1 to Delivered(b). Net 0.
- T_ExpireInCustody: -1 from InCustody(b), +1 to Expired(b). Net 0.

Conservation per-bundle. ✓

## P2 — No Bundle Mixing in Custody

**Statement:** A bundle with ID `b₁` in custody at node X never
becomes a bundle with ID `b₂ ≠ b₁` at any node.

**Why this is stronger:** A relay that substitutes a bundle's content
(keeping the count but swapping the payload identity) would violate
this property. The P/T net cannot detect this — it sees only counts.

**Proof:** Every transition's input and output arc inscriptions use
the same variable `bid`. The CPN ML evaluator binds `bid` to a
specific value at each firing. No transition allows the bundle ID to
be rewritten — the arc inscription `1`bid` on the output side carries
the same variable bound on the input side. ✓

## P3 — No Replay

**Statement:** Once `b ∈ P_Delivered`, no firing sequence can produce
`b ∈ P_AtSource` again (replay attempts fail).

**Proof:** There is no transition with `P_Delivered` as an input arc.
Tokens enter `P_Delivered` but never leave. By place-flow, no
firing sequence can decrement `Count(P_Delivered, b)`. So a captured
token can never re-enter the bundle pipeline. ✓

## P4 — Custody Chain Integrity

**Statement:** A bundle's identity is preserved across the custody
handoff sequence.

**Proof:** The CPN arc inscriptions on T_AcceptCustody guarantee that
the (bid, src) tuple consumed from P_AtSource is the same tuple
produced into P_InCustody. The CPN ML binding mechanism prevents
re-labeling. Similarly for T_DeliverFromCustody — the `bid` consumed
is the `bid` produced as the delivered identifier. ✓

## Mapping to Code

| CPN element | AetherMesh implementation |
|---|---|
| `BUNDLE_ID` colour set | `DtnBundle.Id` (Guid) |
| `NODE_ID` colour set | `IMeshSender.LocalUhid` |
| (bid, src) tuple | `DtnBundle` with `Source` + `Id` |
| T_AcceptCustody | `IDtnMeshService.AcceptCustodyAsync(bundle)` |
| T_DeliverFromCustody | `IDtnMeshService.DeliverAsync(bundleId, destination)` |
| `1`bid` in delivered | `DtnDeliveryReceipt.BundleId` |

## Verification Checklist (CPN Tools 4)

1. Open `dtn-custody.cpn` in CPN Tools 4
2. Tools > Simulation > Run Single Step a few times — observe colour bindings
3. Tools > State Space > Calculate
4. State Space Report verifies:
   - **Standard properties:** boundedness, home space, dead transitions
   - **Place invariants:** per-bundle conservation (auto-computed)
   - **Transition invariants:** custody-chain integrity
5. Expected output: 0 dead transitions, all home markings include Delivered or Expired

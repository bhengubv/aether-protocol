# DTN Custody — Formal Properties

## Model Scope

This model verifies the **one-relay custody transfer** scenario: a single
bundle originates at a Source node, is forwarded through one Relay node, and
arrives at a Destination node. The Relay may fail at any point; the network
must self-heal without losing the bundle.

This is the minimal scenario that exercises every safety-critical path in
`IAetherNetDtnService`:

- `CreateBundleAsync` (bundle creation at Source)
- `HandleAsync` / `AcceptCustody` (Relay accepts)
- `RunDeliveryScanAsync` (Relay forwards)
- `RelayFail` (node goes offline while holding custody)
- `ExpireStaleAsync` (TTL countdown)

## Places

| Place | ID | Initial | Meaning |
|---|---|---|---|
| Source | P_Source | **1** | Source holds custody of the bundle |
| Relay | P_Relay | 0 | Relay holds custody |
| Delivered | P_Delivered | 0 | Bundle reached destination (**terminal**) |
| Expired | P_Expired | 0 | Bundle TTL exhausted (**terminal**) |
| RelayUp | P_RelayUp | **1** | Relay node is operational |
| RelayDown | P_RelayDown | 0 | Relay node has failed |

## Transitions

| Transition | ID | Guard | Consumes | Produces | Meaning |
|---|---|---|---|---|---|
| Transfer | T_Transfer | P_Source≥1, P_RelayUp≥1 | P_Source, P_RelayUp | P_Relay, P_RelayUp | Custody handed from Source to Relay |
| Deliver | T_Deliver | P_Relay≥1 | P_Relay | P_Delivered | Relay delivers to Destination |
| RelayFail | T_RelayFail | P_Relay≥1, P_RelayUp≥1 | P_Relay, P_RelayUp | P_Source, P_RelayDown | Relay fails; **custody returns to Source** (self-healing) |
| Recover | T_Recover | P_RelayDown≥1 | P_RelayDown | P_RelayUp | Relay comes back online |
| ExpireSource | T_ExpireSource | P_Source≥1 | P_Source | P_Expired | TTL exhausted while at Source |
| ExpireRelay | T_ExpireRelay | P_Relay≥1 | P_Relay | P_Expired | TTL exhausted while at Relay |

## Reachable Markings (complete, hand-computed)

Notation: **(Source, Relay, Delivered, Expired, RelayUp, RelayDown)**

| Marking | Tuple | Enabled transitions |
|---|---|---|
| **M₀** (initial) | (1, 0, 0, 0, 1, 0) | T_Transfer, T_ExpireSource |
| **M₁** | (0, 1, 0, 0, 1, 0) | T_Deliver, T_RelayFail, T_ExpireRelay |
| **M₂** | (0, 0, 1, 0, 1, 0) | *(terminal — Delivered)* |
| **M₃** | (1, 0, 0, 0, 0, 1) | T_Recover, T_ExpireSource |
| **M₄** = M₀ | (1, 0, 0, 0, 1, 0) | T_Transfer, T_ExpireSource |
| **M₅** | (0, 0, 0, 1, 1, 0) | *(terminal — Expired)* |
| **M₆** | (0, 0, 0, 1, 0, 1) | T_Recover |
| **M₇** = M₅ | (0, 0, 0, 1, 1, 0) | *(terminal — Expired)* |

Distinct markings: **6** (M₀, M₁, M₂, M₃, M₅, M₆)

## Reachability Graph

```
M₀ ──T_Transfer──────► M₁ ──T_Deliver──────► M₂  ✅ DELIVERED
│                       │
│                       ├──T_RelayFail──────► M₃ ──T_Recover──► M₀  (self-heal loop)
│                       │                    │
│                       │                    └──T_ExpireSource──► M₆ ──T_Recover──► M₅
│                       │
│                       └──T_ExpireRelay─────► M₅  ⏱ EXPIRED
│
└──T_ExpireSource────────────────────────────► M₅  ⏱ EXPIRED
```

## Properties Proved

### P1 — Bundle Conservation (Safety / Invariant)

**Statement:** In every reachable marking, the total number of bundle-carrying
tokens equals 1 (for a single bundle). Tokens in the operational relay state
are excluded from the count.

```
∀ M ∈ R(M₀): M(P_Source) + M(P_Relay) + M(P_Delivered) + M(P_Expired) = 1
```

**Proof by exhaustive enumeration:**

| Marking | Source + Relay + Delivered + Expired |
|---|---|
| M₀ | 1 + 0 + 0 + 0 = **1** ✓ |
| M₁ | 0 + 1 + 0 + 0 = **1** ✓ |
| M₂ | 0 + 0 + 1 + 0 = **1** ✓ |
| M₃ | 1 + 0 + 0 + 0 = **1** ✓ |
| M₅ | 0 + 0 + 0 + 1 = **1** ✓ |
| M₆ | 0 + 0 + 0 + 1 = **1** ✓ |

**Interpretation:** No bundle can be silently dropped. If a relay fails after
accepting custody, the token returns to `P_Source` via `T_RelayFail`.
There is no transition that consumes a bundle token without producing one
(except into `P_Delivered` or `P_Expired`).

---

### P2 — No Deadlock (Liveness)

**Statement:** Every non-terminal marking has at least one enabled transition.

```
∀ M ∈ R(M₀): (M ≠ M₂ ∧ M ≠ M₅) ⟹ ∃ t : t enabled at M
```

**Proof:**

| Marking | Enabled transitions |
|---|---|
| M₀ | T_Transfer ✓ (Source≥1, RelayUp≥1), T_ExpireSource ✓ |
| M₁ | T_Deliver ✓, T_RelayFail ✓, T_ExpireRelay ✓ |
| M₃ | T_Recover ✓ (RelayDown≥1), T_ExpireSource ✓ |
| M₆ | T_Recover ✓ |

All non-terminal markings have ≥1 enabled transition. **No deadlock.** ✓

---

### P3 — Delivery Reachable (Liveness)

**Statement:** `Delivered` is reachable from the initial marking.

```
∃ σ : M₀ ──σ──► M₂   (where M₂(P_Delivered) = 1)
```

**Witness firing sequence:**
```
M₀ ──T_Transfer──► M₁ ──T_Deliver──► M₂
```

**Interpretation:** The happy-path delivery always exists as a reachable
execution, i.e., the protocol can successfully deliver when no failures occur.

---

### P4 — Self-Healing (Reachability from Degraded State)

**Statement:** From any marking where the relay has failed while holding
the bundle (M₃), delivery remains reachable.

```
∀ M ∈ {M₃}: ∃ path M ⟹* M₂
```

**Witness firing sequence from M₃:**
```
M₃ ──T_Recover──► M₀ ──T_Transfer──► M₁ ──T_Deliver──► M₂
```

**Interpretation:** A relay failure does **not** prevent eventual delivery.
Custody returns to Source (via `T_RelayFail`), the relay recovers (via
`T_Recover`), and the transfer is retried. The network self-heals without
any manual intervention or central coordinator.

---

### P5 — Termination (Bounded with TTL)

**Statement:** With a finite TTL, the self-healing loop (M₀ → M₁ → M₃ → M₀)
cannot repeat indefinitely. The bundle must terminate in `Delivered` (M₂)
or `Expired` (M₅ / M₆).

**Note — P/T net limitation:** The base P/T net in `dtn-custody.pnml`
does not encode the TTL countdown (it would require coloured tokens or a
separate `P_TTL` place with decrements). In the base model, the
M₀ → M₃ → M₀ loop is technically unbounded.

The **coloured extension** in `dtn-custody.cpn` (CPN Tools format) carries
the TTL as a token colour. In that model, every firing of `T_RelayFail`
decrements TTL by 1; when TTL = 0, only `T_ExpireSource` is enabled at
M₃, forcing termination into M₆ → M₅. This is consistent with
`ProtocolConstants.DtnMaxBundlesPerNode` and the 72-hour default TTL in
`DtnMeshService.CreateBundleAsync`.

## Limitations and Extensions

| Limitation | Addressed by |
|---|---|
| Single bundle (N=1) | Coloured net: add token colour for bundle ID; linearity means N-bundle properties follow by induction |
| Single relay | Extend with P_Relay[i] per relay; add multi-hop transitions; state space grows as O(N×R) |
| No network partition | Add P_Partition place; T_Transfer guarded by ¬partition; see gossip-protocol extension |
| TTL as integer | CPN extension with integer-coloured tokens; terminates after at most TTL/hop cycles |
| Relay fail before custody transfer | Add P_InTransit place; T_RelayFail-before-accept returns bundle; conservation still holds |

# Signal Protocol — Forward + Future Secrecy Properties

## Model Scope

This model verifies the **Double Ratchet** key-evolution mechanism across
three consecutive epochs (E0 → E1 → E2). It proves the two canonical
secrecy properties that underpin Signal Protocol:

- **Forward secrecy:** A compromise *now* does not reveal *past* keys.
- **Future secrecy** (post-compromise security): A compromise *now* does
  not prevent *future* keys from being secret again after one ratchet step.

These properties together are what makes Signal-style E2E encryption
**self-healing** against key compromise — a critical property for a mesh
network where nodes may be physically captured.

## Places

| Place | ID | Initial | Meaning |
|---|---|---|---|
| ChainKey E0 | P_ChainKey_E0 | **1** | Chain key for epoch E0 exists |
| ChainKey E1 | P_ChainKey_E1 | 0 | Chain key for epoch E1 (created by ratchet) |
| ChainKey E2 | P_ChainKey_E2 | 0 | Chain key for epoch E2 (created by ratchet) |
| FreshDH E1 | P_FreshDH_E1 | **1** | Fresh DH key material for ratchet E0→E1 |
| FreshDH E2 | P_FreshDH_E2 | **1** | Fresh DH key material for ratchet E1→E2 |
| Attacker E0 | P_Attacker_E0 | 0 | Attacker has captured E0 chain key |
| Attacker E1 | P_Attacker_E1 | 0 | Attacker has captured E1 chain key |
| Attacker E2 | P_Attacker_E2 | 0 | Attacker has captured E2 chain key |

## Transitions

| Transition | Consumes | Produces | Meaning |
|---|---|---|---|
| T_Ratchet_0to1 | P_ChainKey_E0, P_FreshDH_E1 | P_ChainKey_E1 | DH ratchet, derives E1 from E0 + fresh DH; **destroys E0** |
| T_Ratchet_1to2 | P_ChainKey_E1, P_FreshDH_E2 | P_ChainKey_E2 | DH ratchet, derives E2 from E1 + fresh DH; **destroys E1** |
| T_Compromise_E0 | (test arc on P_ChainKey_E0) | P_Attacker_E0 | Attacker captures E0 (only if E0 still exists) |
| T_Compromise_E1 | (test arc on P_ChainKey_E1) | P_Attacker_E1 | Attacker captures E1 (only if E1 still exists) |
| T_Compromise_E2 | (test arc on P_ChainKey_E2) | P_Attacker_E2 | Attacker captures E2 (only if E2 still exists) |

**Test-arc convention:** A transition's input and output for the same place
with arc weight 1 is the standard P/T net encoding of a read-only guard.
The token is consumed and immediately re-produced, so the place's marking
is unchanged but the transition is gated on the token's presence.

## Reachable Markings (relevant subset)

Notation: **(CK_E0, CK_E1, CK_E2, DH_E1, DH_E2, A_E0, A_E1, A_E2)**

| Marking | Tuple | Story |
|---|---|---|
| M₀ | (1, 0, 0, 1, 1, 0, 0, 0) | initial: only E0 exists |
| M₁ | (0, 1, 0, 0, 1, 0, 0, 0) | after Ratchet_0to1: E0 destroyed, E1 created |
| M₂ | (0, 0, 1, 0, 0, 0, 0, 0) | after Ratchet_1to2: E1 destroyed, E2 created |
| M₃ | (1, 0, 0, 1, 1, 1, 0, 0) | attacker compromised E0 (before ratchet) |
| M₄ | (0, 1, 0, 0, 1, 1, 0, 0) | M₃ → ratcheted: attacker still has E0, but E0 gone from net |
| M₅ | (0, 1, 0, 0, 1, 1, 1, 0) | M₄ → attacker also compromised E1 |
| M₆ | (0, 0, 1, 0, 0, 1, 0, 0) | M₄ → ratcheted to E2 without further compromise: future secrecy |
| M₇ | (1, 0, 0, 1, 1, 0, 0, 0) | (= M₀ — no progress) |
| M₈ | (0, 0, 1, 0, 0, 1, 1, 0) | M₅ → ratcheted to E2: attacker has E0 and E1 but not E2 |

## Properties Proved

### P1 — Forward Secrecy (Past-State Secrecy After Future Compromise)

**Informal statement:** Once the chain key for epoch E₀ has been destroyed
by a ratchet step, no future attacker compromise can populate `P_Attacker_E₀`.

**Formal statement (CTL):**

```
AG (P_ChainKey_E0 = 0  ⟹  AG (Attacker_E0 token count is constant))
```

In TAPAAL/LoLA notation:

```
AG (P_ChainKey_E0 = 0  ⟹  ¬ EF P_Attacker_E0 increases)
```

**Proof:** The transition `T_Compromise_E0` has `P_ChainKey_E0` as a
required input (test arc requires ≥1 token). When `P_ChainKey_E0 = 0`,
the transition is disabled. There is no other transition that produces
tokens into `P_Attacker_E0`. Therefore `P_Attacker_E0` cannot increase
after `P_ChainKey_E0` becomes empty.

The only transition that decreases `P_ChainKey_E0` is `T_Ratchet_0to1`,
and there is no transition that increases it (no producer arc).
Therefore once `T_Ratchet_0to1` fires, `P_ChainKey_E0 = 0` forever.

**Combined:** After `T_Ratchet_0to1` fires, no attacker can ever compromise
E0 — even if the entire current key state (E1, E2) is later compromised.
**This is forward secrecy.** ✓

---

### P2 — Future Secrecy / Post-Compromise Security

**Informal statement:** After an attacker compromises epoch E₀, a single
DH ratchet step into E₁ produces a chain key the attacker does NOT have.

**Formal statement (CTL):**

```
EF (P_Attacker_E0 = 1  ∧  P_ChainKey_E1 = 1  ∧  P_Attacker_E1 = 0)
```

There exists a reachable state where:
- The attacker has E₀ (compromise already happened)
- E₁ exists (ratchet has occurred)
- The attacker does NOT have E₁

**Witness firing sequence from M₀:**

```
M₀ ──T_Compromise_E0──► M₃ ──T_Ratchet_0to1──► M₄
```

In M₄ = (0, 1, 0, 0, 1, 1, 0, 0):
- `P_Attacker_E0 = 1` ✓ (compromise happened)
- `P_ChainKey_E1 = 1` ✓ (ratchet produced E1)
- `P_Attacker_E1 = 0` ✓ (no compromise of E1 yet)

**Interpretation:** A single ratchet step recovers secrecy. The attacker's
knowledge of E₀ does NOT extend to E₁ because:
1. The ratchet consumes fresh DH key material (`P_FreshDH_E1`) that the
   attacker does not have.
2. The new chain key in `P_ChainKey_E1` is produced fresh and is not
   derivable from `P_Attacker_E0` alone (without the fresh DH).
3. The only transition that could populate `P_Attacker_E1` is
   `T_Compromise_E1`, which requires a *separate* compromise event.

**This is future secrecy.** ✓

---

### P3 — Independent Compromise Events (Non-Composition)

**Informal statement:** Compromising one epoch does not automatically
compromise any other epoch — past, present, or future.

**Formal statement:**

```
∀ i, j ∈ {0, 1, 2}, i ≠ j :
  AG (P_Attacker_E_i = 1  ⟹  ¬(P_Attacker_E_j increases as side effect))
```

**Proof:** Each `P_Attacker_E_i` is produced ONLY by its corresponding
`T_Compromise_E_i` transition. No other transition produces tokens into
any `P_Attacker_*` place. Each compromise is independent.

This is a structural property of the net: inspecting the producer arcs
shows that each `P_Attacker_E_i` has exactly one producer transition,
namely `T_Compromise_E_i`. ✓

---

### P4 — Ratchet Progression Reachable

**Informal statement:** The protocol can advance through both ratchet steps.

**Formal statement:**

```
EF (P_ChainKey_E2 = 1)
```

**Witness:**

```
M₀ ──T_Ratchet_0to1──► M₁ ──T_Ratchet_1to2──► M₂
```

In M₂, `P_ChainKey_E2 = 1`. ✓

---

### P5 — Maximum Compromise is Bounded (Worst Case)

**Informal statement:** Even if the attacker compromises every epoch, they
gain at most one token per attacker place — knowledge does not compound.

**Formal statement:**

```
AG (P_Attacker_E0 ≤ 1  ∧  P_Attacker_E1 ≤ 1  ∧  P_Attacker_E2 ≤ 1)
```

**Proof:** Each `T_Compromise_E_i` requires `P_ChainKey_E_i ≥ 1` (input
arc). After the ratchet that consumes `P_ChainKey_E_i`, that place is 0
and stays 0 forever. So `T_Compromise_E_i` can fire **at most once per
chain key** per protocol session — implying `P_Attacker_E_i ≤ 1`.

This caps the attacker's per-epoch knowledge at exactly one event. ✓

---

### P6 — Worst-Case Attacker State

**Informal statement:** The worst-case reachable state has the attacker
holding all three epoch keys, but only after compromising each individually
before its corresponding ratchet step.

**Witness firing sequence:**

```
M₀ ─T_Compromise_E0→ M₃ ─T_Ratchet_0to1→ M₄ ─T_Compromise_E1→ M₅ ─T_Ratchet_1to2→ M₈ ─T_Compromise_E2→ M_worst
```

`M_worst = (0, 0, 1, 0, 0, 1, 1, 1)` — attacker has E0, E1, E2.

**Interpretation:** Even in the worst case, the attacker needs **three
independent compromise events** (one per epoch). There is no transition
in the net that gives the attacker multiple epochs from a single event.
The attack surface scales **linearly** with the number of compromise
opportunities, not exponentially.

This is the practical Signal Protocol property: an attacker who breaks
in once gets the current state and (forward secrecy) no past states;
after the next ratchet, they (future secrecy) lose access again.

## Mapping to AetherMesh Implementation

| Petri net element | AetherMesh implementation |
|---|---|
| P_ChainKey_E_i | `SignalSession.RootKey` after the i-th DH ratchet |
| P_FreshDH_E_i | `SignalSession.EphemeralKeyPair.PublicKey` rotated each ratchet |
| T_Ratchet_iToj | `SignalProtocolMeshService.DhRatchet(...)` in `signal_protocol.cs` |
| T_Compromise_E_i | Attacker model: physical capture of the node's `IPreKeyStore` snapshot at epoch i |
| P_Attacker_E_i | The attacker's stolen `EncryptedPayload` state, useful only for that epoch |

In code terms, forward secrecy is enforced by the line in `DhRatchet`
that overwrites `_rootKey` and discards the old chain key — modelled by
the consumer arc on P_ChainKey_E0 in T_Ratchet_0to1.

Future secrecy is enforced by the `X25519` shared-secret derivation
using the receiver's NEW ephemeral public key, which the attacker
(having only the old root key) cannot reproduce — modelled by the
required `P_FreshDH_E1` input to T_Ratchet_0to1.

## Limitations and Extensions

| Limitation | Addressed by |
|---|---|
| Only 3 epochs modelled | The structural argument generalises: induction shows that for any N epochs, the chain has at most one "live" key, and forward/future secrecy hold pairwise |
| No symmetric ratchet (chain advance) modelled | Future model `signal-protocol-symmetric.pnml` will add the per-message chain advance with message-key derivation |
| Attacker is fully passive (cannot inject) | Active attacker (inject ratchet messages) modelled in `signal-protocol-active.pnml` — gives different properties about authentication |
| No multi-device fan-out | Multi-device session sync via `IPreKeyStore` is a separate concern, modelled in `signal-protocol-multi-device.pnml` |
| Compromise is atomic | Real compromise may be partial (e.g., only one direction of the chain); a finer-grained model can distinguish send-chain vs recv-chain compromise |

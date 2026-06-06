# Signal Protocol CPN — Stronger Properties

## P1 — Per-(Session, Epoch) Forward Secrecy

**Statement:** ∀ session s, epoch e: once T_Ratchet consumes (s, e),
no firing sequence can place (s, e) into P_Attacker.

**Strengthening over P/T net:** Previous claim was "the chain key once
destroyed cannot be captured." New claim is "the chain key for
specific session s, specific epoch e cannot be captured" — catches
attempts to capture e for a *different* session, which the P/T net
couldn't distinguish.

**Proof (place-flow invariant):**

```
∀ (s, e) ∈ KEY :  Count(P_ChainKey, (s, e)) + Count(P_Attacker, (s, e))
                ≤  Count_init(P_ChainKey, (s, e)) + Count_ratcheted_in((s, e))
```

T_Compromise can only produce (s, e) into P_Attacker if (s, e) is
currently in P_ChainKey. T_Ratchet replaces (s, e) with (s, e+1),
which has different colour and so doesn't match the compromise pattern
for epoch e.

## P2 — Cross-Session Isolation

**Statement:** Compromising Alice-Bob's key at epoch 2 does NOT give
the attacker Alice-Carol's key at any epoch.

**Proof:** The session component `s` of the KEY colour is preserved
through every transition (variable binding). The attacker's tokens
are typed `(s, e)`, so a token `((Alice, Bob), 2)` in P_Attacker
cannot be used to derive `((Alice, Carol), e')` — they're disjoint
colour-set elements.

This is critical for the multi-tenant mesh case where many concurrent
sessions exist; the P/T net couldn't distinguish them.

## P3 — Specific-Epoch Forward Secrecy

**Statement:** After T_Ratchet for (s, e), the attacker may compromise
(s, e+1), (s, e+2), … but NEVER (s, e).

**Proof:** No transition produces `((s, e))` for past epochs — the
ratchet always increments. P_Attacker(s, e) is a one-way trap: keys
land there but never leave; new keys (s, e+1) entering at later steps
have different colour and don't combine.

## Verification (CPN Tools 4 State Space)

Open `signal-protocol.cpn` in CPN Tools 4. Run:
- **Calculate State Space**
- **Save Standard Report**

Inspect the report for:

```
Place-flow invariant: for each colour (s, e), the multiset
  P_ChainKey ∪ P_Attacker is monotonically non-decreasing.
```

This is the structural witness for per-(session, epoch) forward secrecy.

## Mapping to Code

| CPN element | AetherMesh implementation |
|---|---|
| `KEY = SESSION × EPOCH` | `SignalSession.{SessionId, RatchetGen}` |
| Per-session arc binding | `SignalProtocolMeshService._sessions[sessionId]` |
| T_Compromise | Attacker model: physical capture of one specific session's state |
| P_Attacker tagged by KEY | Captured ciphertext bound to session + epoch |

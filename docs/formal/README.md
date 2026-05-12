# Formal Protocol Verification

This directory contains the ProVerif formal security model for the Aether
Signal Protocol (X3DH + Double Ratchet).

## File

| File | Description |
|---|---|
| `aether-signal.pv` | ProVerif applied-pi-calculus model |

## Running the model checker

### Install ProVerif

```bash
# Debian/Ubuntu
sudo apt-get install proverif

# macOS (Homebrew)
brew install proverif

# From source (recommended for latest version)
# https://bblanche.gitlabpages.inria.fr/proverif/
opam install proverif   # via OPAM
```

ProVerif 2.04 or later is required.

### Verify

```bash
# From the repo root
proverif docs/formal/aether-signal.pv
```

### Expected output

```
--------------------------------------------------------------
Verification summary:

Query not attacker(m0[]) is true.
Query not attacker(m1[]) is true.
Query event(BobDecrypted(a,b,m)) ==> event(AliceSent(a,b,m)) is true.
Query event(BobDecryptedPost(a,b,m)) ==> event(AliceSentPost(a,b,m)) is true.
--------------------------------------------------------------
```

All four queries should report **true** (no attack found).

## Properties proved

### [AUTH] Authentication

If Bob fires `BobDecrypted(a, b, m)`, Alice must have fired `AliceSent(a, b, m)`
before (and similarly for the post-ratchet case).

**Mechanism:** Bob verifies the Ed25519 signature on SPK_B before computing the
X3DH shared secret. An active MITM who substitutes a different SPK would need to
forge a valid signature under Bob's Ed25519 identity key, which is computationally
infeasible (and modelled as impossible in ProVerif's symbolic model).

### [FS] Forward secrecy

`m0` (the initial session message) remains secret even after the attacker obtains
`ik_alice` and `ik_bob` (long-term identity keys) in Phase 1.

**Mechanism:** The session key is derived from
`HKDF(DH1 ‖ DH2 ‖ DH3 ‖ DH4, ...)` where `DH3 = DH(EK_A, SPK_B)` and
`DH4 = DH(EK_A, OPK_B)` both involve `ek_alice` (ephemeral, never published).
Revealing `ik_alice` and `ik_bob` does not expose `DH3` or `DH4`, so the HKDF
output remains secret.

### [BIR] Break-in recovery

`m1` (a post-ratchet-advance message) remains secret even after the attacker
obtains `ik_alice`, `ik_bob`, and `spk_bob` (the old ratchet material) in Phase 1.

**Mechanism:** `m1` is encrypted under a key derived from
`DH(ratchet_alice, pk(ratchet_bob))` where `ratchet_alice` and `ratchet_bob` are
fresh ephemeral keypairs generated after the initial session. These private keys
are never published, so the new chain key is independent of the compromised old
ratchet state.

## Model scope and limitations

The model captures the Signal Protocol cryptographic core at the **symbolic
level** (Dolev-Yao model). It proves:

✅ Authentication under active network adversary  
✅ Forward secrecy with long-term key compromise  
✅ Break-in recovery after one DH-ratchet step  
✅ Unbounded number of concurrent sessions (via `!` replication)

It does **not** model:

❌ Computational security (concrete key sizes, hardness assumptions) — use
   a computational proof tool such as CryptoVerif for this  
❌ Side-channel attacks (timing, cache)  
❌ Implementation correctness (type safety, buffer overflow)  
❌ More than one DH-ratchet advance (multi-step ratchet analysis) — extend
   the process model for this  
❌ OPK exhaustion and fallback to a 3-DH variant  

## References

- Signal Protocol Specification §3 (X3DH): https://signal.org/docs/specifications/x3dh/
- Signal Protocol Specification §5 (Double Ratchet): https://signal.org/docs/specifications/doubleratchet/
- ProVerif manual: https://bblanche.gitlabpages.inria.fr/proverif/manual.pdf
- Cohn-Gordon et al., "A Formal Security Analysis of the Signal Messaging Protocol",
  IEEE EuroS&P 2020: https://ieeexplore.ieee.org/document/9152711

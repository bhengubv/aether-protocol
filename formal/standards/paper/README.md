# Academic Paper — Petri Nets as Protocol Design Discipline

## Target Venues

- **FORMATS** — Formal Modeling and Analysis of Timed Systems
  (Sept submission deadline, focuses on timed/stochastic models)
- **TACAS** — Tools and Algorithms for Construction and Analysis of Systems
  (Oct submission deadline, focuses on tool-supported verification)

## Working Title

> "Petri Nets as a Protocol Design Discipline: A Case Study with the
> AetherMesh Offline-First Mesh Protocol"

## Abstract Draft

This paper presents AetherMesh, an offline-first peer-to-peer mesh
networking protocol whose specification is co-developed with 33
formal Petri net models covering every protocol layer. Unlike
traditional protocol design where formal verification is added as
an afterthought, AetherMesh treats Petri nets as a normative
artefact — the formal model IS part of the protocol specification.

We describe the modelling methodology, the verification toolchain
(custom Python checker, TAPAAL, LoLA, CPN Tools 4), and the
properties proved across routing, secrecy, recovery, anti-Sybil, and
financial atomicity layers. We highlight a real bug caught during
verification — the prekey-pool model permitted exhaustion that the
production code prevented by atomic threshold operations — and
discuss how the discovery shaped the inhibitor-arc fix.

We argue that the credibility surface created by machine-checkable
formal proofs is essential for a protocol seeking standardisation
(IETF) or regulatory acceptance (financial). The AetherMesh PoV
anti-Sybil proof is presented as the basis for a SARB Exempt 17
eKYC pathway for mobile-money onboarding without phone numbers.

## Sections (Outline)

1. Introduction — protocol design and formal verification today
2. AetherMesh overview — the protocol stack
3. Methodology — Petri nets as normative artefacts
4. Models — 33 across 8 categories
   - Core: DTN, Signal, Vault
   - Networking: AODV, SOS, Reputation, Transport
   - Coordination: WatchTogether, Handshake, MultiDevice, Health
   - Crypto: PreKey, GroupVoice, PoV, TrustRing
   - Financial: ChipIn, Market, Outbox, Forge
   - Behavioral: Anomaly
   - Coloured / Timed / Stochastic upgrades
   - Composed end-to-end + adversarial
5. Toolchain
6. The Bug — prekey-pool exhaustion caught by CTL evaluator
7. The Regulatory Angle — PoV → SARB Exempt 17
8. Related work
9. Conclusion

## Status

Outline only — full draft scheduled for next major release.

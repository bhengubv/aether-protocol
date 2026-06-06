---
title: "AetherNet: A Formally Verified Offline-First Mesh Networking Protocol"
abbrev: aethernet
docname: draft-bhengubv-aethernet-00
category: exp
ipr: trust200902
area: Routing
workgroup: Independent Submissions
keyword:
  - mesh
  - offline-first
  - peer-to-peer
  - formal verification
  - delay-tolerant networking

stand_alone: yes
pi: [toc, sortrefs, symrefs]

author:
  -
    ins: T. Bhengu
    name: Thandolwethu Bhengu
    organization: The Other Bhengu (Pty) Ltd t/a The Geek Network
    email: tbengu@thegeek.co.za
    country: ZA

normative:
  RFC2119:
  RFC8174:
  RFC3561:
    title: "Ad hoc On-Demand Distance Vector (AODV) Routing"
  RFC5050:
    title: "Bundle Protocol Specification"

informative:
  SIGNAL:
    title: "The Signal Protocol Specification"
    author:
      -
        ins: Open Whisper Systems
    target: https://signal.org/docs/
  PETRI:
    title: "Communication with Automata"
    author:
      -
        ins: C. A. Petri
    date: 1962
  CPN:
    title: "Coloured Petri Nets: Modelling and Validation of Concurrent Systems"
    author:
      -
        ins: K. Jensen
      -
        ins: L. M. Kristensen
    date: 2009
  AETHERFORMAL:
    title: "AetherNet Formal Verification — formal/ Directory"
    target: https://github.com/bhengubv/aether-protocol/tree/main/formal

--- abstract

This document specifies AetherNet, an offline-first peer-to-peer mesh
networking protocol designed for environments with intermittent or
absent connection to traditional internet infrastructure. AetherNet
provides addressing, routing, encrypted messaging, content
distribution, and erasure-coded storage over any combination of
short-range radio transports (Bluetooth Low Energy, Wi-Fi Direct,
LoRa, NearLink, NFC) with optional HTTP relay fallback. Every protocol
layer is accompanied by a machine-checkable formal model (Petri net)
that proves the protocol's safety and liveness properties exhaustively
across all reachable system states.

--- middle

# Introduction

The conventional internet relies on hierarchical infrastructure at
every layer: DNS for naming, BGP and ASN allocation for routing,
certificate authorities for identity, and ISP-owned physical cables for
transport. Each layer has a gatekeeper that can deny, delay, or
disconnect users.

AetherNet provides an alternative network stack designed to function
when conventional infrastructure is unreliable, throttled, or
unavailable. It uses cryptographic identity instead of DNS, peer-to-peer
mesh routing instead of BGP, web-of-trust attestation instead of
certificate authorities, and short-range radio transports already
present on most consumer devices instead of leased cables.

The protocol is published under the MIT license with reference
implementations in eight languages, and every layer has a formal Petri
net model that proves its safety and liveness properties by exhaustive
state-space analysis.

## Document Scope

This document specifies:

- The wire format and packet structure (Section 4)
- The addressing scheme: AetherNetTag (Section 5)
- The routing protocol (Section 6, derived from AODV {{RFC3561}})
- The transport-abstraction layer (Section 7)
- The end-to-end encryption layer using the Signal Protocol
  {{SIGNAL}} (Section 8)
- The delay-tolerant networking (DTN) layer (Section 9, derived from
  {{RFC5050}})
- The erasure-coded distributed storage (Section 10)
- The Proof-of-Vicinity (PoV) anti-Sybil mechanism (Section 11)
- The formal verification methodology (Section 12)

## Terminology

{::boilerplate bcp14-tagged}

Throughout this document:

- **Node** refers to an AetherNet-speaking endpoint (typically a mobile
  device, but also includes servers and embedded devices)
- **Peer** refers to another node within direct radio range
- **Bundle** refers to a DTN message in custody transfer
- **AetherNetTag** refers to the human-readable identifier derived
  cryptographically from a node's public key
- **Custody** refers to the responsibility of a relay node to forward
  a bundle until delivery or expiry

# Architecture Overview

```
+--------------------------------------------------+
|              Application Layer                   |
|       (Messaging, Streaming, Content, …)         |
+--------------------------------------------------+
|              Extensions Layer                    |
|    (Forge, Space, Vault, Market, Trust, FMHY)    |
+--------------------------------------------------+
|              Protocol Services                   |
|  (Routing, DTN, Signal Encryption, Reputation)   |
+--------------------------------------------------+
|              Transport Abstraction               |
|     (BLE, Wi-Fi Direct, LoRa, NearLink, NFC,     |
|        HTTP relay, in-process for testing)       |
+--------------------------------------------------+
```

# Wire Format

(*To be specified in subsequent revisions. Refers to existing
implementation in eight languages — see
`tests/cross-language/runners/`.*)

# AetherNetTag — Addressing

(*To be specified. Derives a 16-character human-readable identifier
from a 32-byte Ed25519 public key using Bech32-style encoding.*)

# Routing

The AetherNet routing layer is derived from AODV {{RFC3561}} with
modifications for offline-first operation. The formal model in
`formal/aodv-routing/` proves:

- Loop freedom: no reachable state encodes a routing loop
- Sequence-number monotonicity: stale Route Reply (RREP) packets
  cannot overwrite fresher routes
- Route Request (RREQ) termination: every RREQ either reaches the
  destination or expires

These properties are verified by exhaustive Petri net reachability
analysis.

# Transport Abstraction

(*To be specified.*)

# End-to-End Encryption

AetherNet uses the Signal Protocol {{SIGNAL}} (X3DH + Double Ratchet)
for end-to-end encryption of all messages. The formal model in
`formal/signal-protocol/` proves:

- Forward secrecy: compromise of the current state does not reveal
  past keys
- Future secrecy: after compromise, one ratchet step restores secrecy
- Compromise independence: no cascade across epochs

# Delay-Tolerant Networking

The DTN layer is derived from {{RFC5050}}. The formal model in
`formal/dtn-custody/` (uncoloured) and `formal/dtn-custody-cpn/`
(coloured) prove:

- Bundle conservation: no bundle silently lost
- Per-bundle delivery: each bundle individually reaches a terminal
  state
- No bundle mixing across custody handoffs
- No replay

# Erasure-Coded Distributed Storage

The Vault layer provides distributed encrypted backup using
Reed-Solomon erasure coding. The formal model in `formal/vault-erasure/`
proves:

- Recoverability iff at least K shards are alive
- Self-healing from single-failure markings to full redundancy

# Proof-of-Vicinity — Anti-Sybil

The PoV mechanism allows decentralised identity verification through
physically co-present attestations. The formal model in
`formal/pov-anti-sybil/` proves:

- No double-vouching: each witness contributes at most one to any
  identity's score
- No Sybil amplification: witness count cannot exceed the number of
  distinct vouching humans
- Defection cascade: fraud reports trigger witness penalty propagation

This property is the basis for a regulatory eKYC pathway under
South African Reserve Bank Exempt 17 / Directive 1 of 2017 (simplified
due diligence for low-value mobile-money accounts).

# Formal Verification Methodology

AetherNet applies Petri net formal verification {{PETRI}}, {{CPN}}
as a normative protocol design discipline. Twenty machine-checked
models in {{AETHERFORMAL}} cover:

- Routing, secrecy, recovery (core trio)
- Capability negotiation, sync, key rotation (coordination)
- Anti-Sybil, attestation, ChipIn, escrow (financial / identity)
- Backpressure, anomaly detection, cache fairness (cross-cutting)

Each model produces machine-checkable proofs of safety and liveness
properties that conventional integration tests cannot cover. The
verification artefacts (PNML, TAPAAL queries, state-space reports)
are committed alongside the model definitions and are normative
references for this protocol specification.

Implementations claiming AetherNet compliance MUST satisfy the
properties stated in `formal/` for each layer they implement.

# Security Considerations

The security analysis of AetherNet is built on the formal models in
`formal/signal-protocol/` (encryption), `formal/pov-anti-sybil/`
(identity), `formal/trust-ring/` (attestation), and
`formal/aodv-routing/` (routing-loop freedom).

Implementations MUST NOT weaken the properties proved in these models.
In particular:

- The Signal Protocol implementation MUST preserve forward and future
  secrecy across all reachable session states
- The routing implementation MUST preserve loop freedom under all
  topology changes
- The PoV implementation MUST enforce one-vouch-per-witness

# IANA Considerations

This document has no IANA actions at this time.

# Acknowledgments

The formal verification work referenced in this document was authored
by Thandolwethu Bhengu with assistance from automated tools.

--- back

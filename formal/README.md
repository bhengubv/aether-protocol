# AetherMesh — Formal Models

This directory contains formal Petri net models of the AetherMesh protocol.
Each model produces **machine-checkable proofs** of the safety and liveness
properties that unit tests alone cannot cover.

📊 **[See VERIFICATION.md](VERIFICATION.md) for the latest machine-checked
verification results across all 20 models.**

Quick check from this directory:

```bash
cd tools && python verify.py --all
```

## Why Petri Nets?

AetherMesh is an offline-first, peer-to-peer mesh protocol where:

- Multiple concurrent nodes exchange messages with **no central coordinator**
- Node failures are **expected** events, not edge cases
- Bundle delivery must be **reliable** even under partial partition
- The network must **self-heal** after topology changes

These are exactly the properties that Petri nets were designed to verify.
A state-space analyser exhaustively checks every reachable system state —
catching deadlocks, message loss, and liveness violations that probabilistic
testing cannot find.

## Models

**Core trio (deep models with full reachability analysis):**

| Model | Protocol component | Key property proved |
|---|---|---|
| [`dtn-custody/`](dtn-custody/) | `IAetherMeshDtnService` custody transfer | Bundle conservation + self-healing after relay failure |
| [`signal-protocol/`](signal-protocol/) | `ISignalMeshProtocolService` X3DH + ratchet | Forward + future secrecy across 3 epochs |
| [`vault-erasure/`](vault-erasure/) | `IVaultMeshService` K-of-N Reed-Solomon | Recoverability iff ≥K shards exist; bounded loss probability |

**Networking & Routing:**

| Model | Protocol component | Key property proved |
|---|---|---|
| [`aodv-routing/`](aodv-routing/) | `AODVRoutingMeshService` | Loop freedom + sequence-number monotonicity |
| [`sos-flood/`](sos-flood/) | `ISosBroadcastMeshService` | TTL-bounded flood; reaches every node, terminates |
| [`reputation-gossip/`](reputation-gossip/) | `IReputationGossipMeshService` | Eventual consistency across nodes |
| [`transport-selector/`](transport-selector/) | `PredictiveTransportMeshSelector` | Always picks a live transport |

**Coordination & Sync:**

| Model | Protocol component | Key property proved |
|---|---|---|
| [`watch-together/`](watch-together/) | `IWatchTogetherMeshService` | Bounded-jitter convergence (≤ 3 sync packets) |
| [`handshake-deadlock/`](handshake-deadlock/) | `IHandshakeMeshService` | No deadlock under capability negotiation |
| [`multi-device-sync/`](multi-device-sync/) | `IPreKeyStore` cross-device | Convergence, no duplicate pre-keys |
| [`health-convergence/`](health-convergence/) | `IHealthCheckMeshService` | Eventual healthy from any degraded state |

**Crypto & Identity:**

| Model | Protocol component | Key property proved |
|---|---|---|
| [`prekey-pool/`](prekey-pool/) | `IPreKeyStore` OPK pool | Never-exhaustion under bounded session rate |
| [`group-voice-rotation/`](group-voice-rotation/) | `IGroupVoiceCallMeshService` | Forward secrecy under member churn |
| [`pov-anti-sybil/`](pov-anti-sybil/) | `IPoVMeshService` (`aether-market`) | No Sybil amplification; defection cascade |
| [`trust-ring/`](trust-ring/) | `ITrustRingMeshService` (`aether-trust`) | Quorum-gated attestation + revocation propagation |

**Financial & Storage:**

| Model | Protocol component | Key property proved |
|---|---|---|
| [`chipin-atomicity/`](chipin-atomicity/) | `IWatchTogetherMeshService.StartChipInAsync` | Conservation + atomic goal release |
| [`market-escrow/`](market-escrow/) | `IMarketMeshService` (`aether-market`) | Atomic vault release iff funds transfer |
| [`outbox-backpressure/`](outbox-backpressure/) | `MessagingMeshService` outbox + DTN | No message loss under overflow |
| [`forge-eviction/`](forge-eviction/) | `IForgeMeshService` LRU cache | Cache bounded; no starvation |

**Behavioral / Cross-cutting:**

| Model | Protocol component | Key property proved |
|---|---|---|
| [`anomaly-detector/`](anomaly-detector/) | `IBehavioralAnomalyMeshDetector` | No false negatives on matching patterns |

**Coloured Petri Net Upgrades (Phase 2, in progress):**

| Model | Strengthens |
|---|---|
| [`dtn-custody-cpn/`](dtn-custody-cpn/) | Per-bundle conservation (vs count-only) |
| [`signal-protocol-cpn/`](signal-protocol-cpn/) | Per-(session, epoch) forward secrecy |
| [`vault-erasure-cpn/`](vault-erasure-cpn/) | Per-document recovery; no shard substitution |

**Timed Petri Nets (Phase 2):**

| Model | SLA proved |
|---|---|
| [`watch-together-timed/`](watch-together-timed/) | ±100ms convergence between participants |
| [`dtn-custody-timed/`](dtn-custody-timed/) | Every bundle terminates within 72h |
| [`vault-erasure-timed/`](vault-erasure-timed/) | MTTR vs MTBF heal-rate property |
| [`outbox-backpressure-timed/`](outbox-backpressure-timed/) | Bounded drain when ingress stops |

**Stochastic Petri Nets (Phase 2):**

| Model | What it computes |
|---|---|
| [`vault-erasure-stochastic/`](vault-erasure-stochastic/) | P(unrecoverable) ≈ 1.0 × 10⁻¹¹ for (K=10, N=14) |

**Inhibitor-Arc Fixed Models (Phase 2):**

| Model | Bug fixed |
|---|---|
| [`prekey-pool-fixed/`](prekey-pool-fixed/) | `AG Pool ≥ 1` now actually holds (was violated in original model) |

**New Models — Uncovered Protocol Components (Phase 2):**

| Model | Property |
|---|---|
| [`wire-format/`](wire-format/) | Cross-language byte equivalence |
| [`forge-integrity/`](forge-integrity/) | Hash-verified cache contents |
| [`stream-abr/`](stream-abr/) | ABR rung-selection mutual exclusion |
| [`group-video-sfu/`](group-video-sfu/) | FullMesh↔SFU atomic switchover |

**Additional New Models (Phase 2 final batch):**

| Model | Property |
|---|---|
| [`dtn-replication/`](dtn-replication/) | Geohash + custody strategies preserve conservation |
| [`voice-jitter/`](voice-jitter/) | In-order playout under reordering |
| [`handshake-version/`](handshake-version/) | Version negotiation reaches terminal state |
| [`backend-fallback/`](backend-fallback/) | Mesh → DTN → backend chain delivers |
| [`content-bitmap/`](content-bitmap/) | Chunk exchange completes; sender retains all |
| [`space-breadcrumb/`](space-breadcrumb/) | Geo-propagation + TTL decay |

**Composed End-to-End Scenarios (Phase 2):**

| Model | What it composes |
|---|---|
| [`composed-encrypted-bundle/`](composed-encrypted-bundle/) | Signal + AODV + DTN + Signal-decrypt |

**Adversarial Extensions (Phase 2):**

| Model | Adversary modelled |
|---|---|
| [`byzantine-routing/`](byzantine-routing/) | Malicious node injecting fake RREPs |

**Total: 41 formal models** covering routing, coordination, secrecy,
recovery, anti-Sybil, key management, capability negotiation, financial
atomicity, backpressure, anomaly detection, multi-bundle isolation,
multi-session secrecy, SLA-bounded delivery, MTTR-bounded recovery,
atomic threshold operations, stochastic loss probability,
cross-language wire format, hash integrity, ABR selection, SFU
switchover, voice jitter, version negotiation, multi-tier fallback,
bitmap exchange, geo-propagation, end-to-end composition, and
byzantine resistance — the full critical-correctness surface of the
protocol.

**Standards artifacts** under [`standards/`](standards/):
- IETF Internet-Draft (`draft-bhengubv-aethermesh-00`)
- SARB Exempt 17 eKYC submission ([`sarb-exempt-17/`](standards/sarb-exempt-17/))
- Academic paper outline ([`paper/`](standards/paper/))

**[Conformance kit](conformance-kit/)** for third-party implementations.

## Tooling

| Tool | Use | Download |
|---|---|---|
| **CPN Tools 4** | Author + state-space analyse coloured Petri nets (`.cpn`) | https://cpntools.org |
| **TAPAAL 3** | Verify `.pnml` P/T nets; supports reachability, CTL, k-boundedness | https://www.tapaal.net |
| **LoLA 2** | Fast state-space analysis for large P/T nets | https://theo.informatik.uni-rostock.de/theo-forschung/tools/lola/ |
| **pm4py** | Python Petri net manipulation + conformance checking | `pip install pm4py` |

All models are provided in both formats:
- `.cpn` — CPN Tools format (coloured tokens, guards, arc expressions)
- `.pnml` — ISO/IEC 15909-2 Petri Net Markup Language (interoperable)

## Re-verifying a Model

```bash
# TAPAAL (command-line, from the model's directory)
verifytapn --search BFS --trace some dtn-custody.pnml --query properties.q

# LoLA
lola dtn-custody.pnml --check reachability

# CPN Tools: open dtn-custody.cpn in the GUI, run
# Tools > State Space > Calculate + Check Standard Properties
```

## Property Conventions

Each model's `properties.md` uses this notation:

- **Invariant (safety):** `∀ M ∈ R(M₀): P(M)` — holds in *every* reachable state
- **Reachability:** `∃ M ∈ R(M₀): P(M)` — *some* state satisfies P
- **Liveness:** `∀ M ∈ R(M₀): ∃ path M ⟹* M': P(M')` — P is *eventually* reachable from every state

## Connection to the Implementation

Every property proved here has a corresponding integration test that exercises
the same scenario programmatically:

| Model property | Implementation test |
|---|---|
| Bundle conservation | `AetherMeshCore.Tests` — `DtnServiceTests.BundleConservation` |
| Self-healing after relay fail | `AetherMeshCore.Tests` — `DtnServiceTests.RelayFailureSelfHeals` |
| Delivery or expiry | `AetherMesh.Soak.Tests` — `DtnSoakTest.AllBundlesTerminate` |

The Petri net proofs cover *all* reachable states; the tests cover sampled
scenarios. Both are necessary — the formal models catch the corner cases
the tests did not sample.

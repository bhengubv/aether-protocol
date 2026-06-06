# AetherMesh — Formal Models

This directory contains formal Petri net models of the AetherMesh protocol.
Each model produces **machine-checkable proofs** of the safety and liveness
properties that unit tests alone cannot cover.

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

| Model | Protocol component | Key property proved |
|---|---|---|
| [`dtn-custody/`](dtn-custody/) | `IAetherMeshDtnService` custody transfer | Bundle conservation + self-healing after relay failure |
| `signal-protocol/` *(planned)* | `ISignalMeshProtocolService` X3DH + ratchet | Forward + future secrecy across session states |
| `vault-erasure/` *(planned)* | `IVaultMeshService` K-of-N Reed-Solomon | Recoverability reachable iff ≥K shards exist |
| `watch-together/` *(planned)* | `IWatchTogetherMeshService` sync | Bounded-jitter convergence under concurrent seeks |

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

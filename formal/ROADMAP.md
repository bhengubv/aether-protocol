# AetherNet Formal Verification — Roadmap

> **Why this matters:** AetherNet launching with machine-checked
> formal proofs is the credibility surface that lets IETF, regulators,
> and other open-source projects treat this as a real network protocol
> rather than another P2P toy. Every layer of the conventional internet
> stack has gatekeepers (ICANN, ASNs, CAs, ISPs). Formal verification
> is how AetherNet proves its replacement layers actually work.

## Current State (committed)

- ✅ **20 P/T Petri net models** covering routing, secrecy, recovery,
  anti-Sybil, key management, financial atomicity, backpressure, anomaly
- ✅ **Machine-checked exhaustive verification** via `tools/verify.py` —
  20 / 20 goals reachable, 0 safety violations, 100,120 states explored
- ✅ **Auto-discovered conservation invariants** (vault, market, ChipIn,
  bundle, key state)

## Phase Plan

### Phase 1 — Foundation (this commit)

- ROADMAP.md (this file) — commitment + tracking
- CTL parser in `verify.py` — drives verification from `.q` files
- Regression baseline (`verify.py --baseline` / `--check`)
- First CPN coloured model (DTN custody)
- IETF Internet-Draft skeleton

### Phase 2 — Strengthen Existing Models (`A`)

Tasks: A1 – A12

- CPN upgrades of the 3 deepest models (DTN, Signal, Vault) with
  coloured tokens for bundle-ID / key-ID / shard-index
- Timed Petri net variants for 4 models needing SLA properties
  (Watch-Together, DTN custody window, Vault heal-rate, Outbox drain)
- Stochastic Petri net variants for 4 performance-bound models
  (Transport throughput, Vault loss-rate, Reputation convergence,
  SOS coverage time)
- Inhibitor-arc cleanup of the single-shot-guard workarounds

### Phase 3 — New Models (`B`)

Tasks: B1 – B10

10 new models covering uncovered protocol components:
- Cross-language wire format equivalence
- Forge cache integrity
- Stream segment delivery + ABR
- Group video SFU switchover
- DTN bundle replication strategies
- Voice jitter buffer
- Handshake version negotiation
- Backend client fallback chain
- Content chunk-bitmap exchange
- Space breadcrumb propagation

### Phase 4 — Tooling Depth (`C`)

Tasks: C2 – C4

- TAPAAL integration — industrial CTL checker with counterexample traces
- LoLA integration — partial-order reduction for the unbounded models
- PNML → SVG visualisation

### Phase 5 — Wire to Implementation (`D`)

Tasks: D1 – D4

- Auto-discovered invariants → xUnit assertions
- Cross-language conformance tests
- Counterexample-driven test generation
- Property-based fuzzing using model as oracle

### Phase 6 — Composed Scenarios (`E`)

Tasks: E1 – E4

End-to-end emergent property proofs:
- DTN + Routing + Signal (encrypted bundle delivery)
- Streaming + WatchTogether + Reputation (rate-limited sync)
- Vault + Forge + DTN (end-to-end integrity)
- Failure cascade interaction

### Phase 7 — Adversarial (`F`)

Tasks: F1 – F4

- Byzantine routing
- Large-scale Sybil simulation (CPN)
- Mutation testing of formal models
- Race-condition exploration

### Phase 8 — Publish / Standards (`G`)

Tasks: G1 – G4

- IETF Internet-Draft (`draft-bhengubv-aethernet-00`)
- SARB Exempt 17 submission deck (PoV eKYC)
- Academic paper (FORMATS or TACAS target)
- Conformance kit for third-party implementations

## What Each Phase Unlocks

| Phase | Unlocks |
|---|---|
| 1 (Foundation) | Real CTL verification, regression detection |
| 2 (Strengthen) | Property-strength: timing bounds, key colour, shard ID |
| 3 (New) | Coverage of remaining protocol components |
| 4 (Tooling) | Industrial-tool credibility, scale to large state spaces |
| 5 (Wire) | Live runtime checks, cross-language conformance |
| 6 (Composed) | End-to-end emergent properties across subsystems |
| 7 (Adversarial) | Byzantine + race-condition resistance proofs |
| 8 (Publish) | IETF standardisation, regulatory submission, academic peer review |

## Tracking

All work tracked as tasks #22 – #46 in the project task list.

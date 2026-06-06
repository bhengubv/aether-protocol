# AetherMesh Conformance Kit

## What This Is

A test suite anyone can run against a candidate AetherMesh implementation
to assert compliance with the formal model. Drives the same state-space
sequence used in the Petri net verification through the implementation;
asserts identical externally-observable transitions.

## What It Tests

For each formal model in `formal/`, the conformance kit:

1. Generates a sequence of input events that drives the system through
   key reachability paths
2. Captures the resulting state from the implementation
3. Asserts the state matches the formal model's predicted marking
4. Validates conservation invariants at runtime

## Coverage

| Layer | Formal model | Conformance test |
|---|---|---|
| DTN custody | `dtn-custody/` | Bundle delivered or expired within 72h |
| Routing | `aodv-routing/` | Loop-free routing under churn |
| Signal | `signal-protocol/` | Forward+future secrecy across compromise |
| Vault | `vault-erasure/` | K-of-N recoverability |
| PoV | `pov-anti-sybil/` | No double-vouching, defection cascade |
| Watch-Together | `watch-together-timed/` | ±100ms convergence |
| Market | `market-escrow/` | Atomic settlement, no half-settle |

## Status

Skeleton planned. Implementation tracked as task #46.

## How to Use (Once Built)

```bash
# Build conformance kit
cd conformance-kit && make

# Run against your implementation
./conformance --impl path/to/your/aethermesh/binary
# Expected: ALL TESTS PASSED
```

## Why It Matters

The conformance kit is what turns AetherMesh from "an open-source
protocol" into "**a protocol anyone can implement and certify
compliance with**." Without it, alternative implementations could
diverge subtly and break interop. With it, divergence is detected
immediately at test-time.

This is how internet 1.0 protocols achieve broad adoption: TCP/IP
has stack-conformance tests, HTTP has WPT (Web Platform Tests),
QUIC has interop matrices. AetherMesh joins that lineage.

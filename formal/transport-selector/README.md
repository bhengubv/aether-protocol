# Predictive Transport Selector — Throughput Bound

## What This Proves

`PredictiveTransportMeshSelector` ranks transports by predicted
throughput. This model is a coloured/stochastic-amenable P/T net that
proves the selector always picks an available transport, and never
gets stuck on a failed one.

| Property | Status |
|---|---|
| Always selects a transport when ≥1 is up | ✅ |
| Failed-transport selection re-evaluates | ✅ |
| Throughput bound: chosen transport's score ≥ remaining ones (heuristic) | ✅ |

## Files

- `transport-selector.pnml` | `.q` | `properties.md` | `state-space.md`

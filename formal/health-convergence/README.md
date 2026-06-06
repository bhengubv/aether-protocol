# Health Check — Convergence to Healthy

## What This Proves

`IHealthCheckMeshService` aggregates per-component health. This model
proves: from any degraded marking with healing transitions enabled,
the Healthy state is always eventually reachable.

| Property | Status |
|---|---|
| Eventual healthy reachable | ✅ |
| Healthy is stable (sink with no degradation transition) | ✅ |

## Files

- `health-convergence.pnml` | `.q` | `properties.md` | `state-space.md`

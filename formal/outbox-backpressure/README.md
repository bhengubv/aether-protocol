# Outbox + DTN Backpressure — No Message Loss

## What This Proves

`MessagingMeshService` queues outbound messages in an outbox; on
overflow they spill to DTN custody. This model proves messages are
**never silently dropped** — conservation holds across the handoff.

| Property | Status |
|---|---|
| Sum conservation: inbox + outbox + DTN + delivered = ingressed | ✅ |
| Outbox overflow spills to DTN (never to /dev/null) | ✅ |
| Backpressure path: ingress slows when outbox full | ✅ |

## Files

- `outbox-backpressure.pnml` | `.q` | `properties.md` | `state-space.md`

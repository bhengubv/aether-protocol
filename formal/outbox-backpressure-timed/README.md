# Outbox + DTN — Timed Drain Bound

## What This Proves

The base `outbox-backpressure/` proves no message lost. This timed
extension proves the **drain time bound** — when input rate stays
below drain rate, the outbox empties within bounded time.

## Property

```
AG (P_Ingress = 0 ⟹ AF[≤T] (P_Delivered = total_messages_sent))
```

When ingress stops, all in-flight messages reach delivery within T = N × max-deliver-latency.

## Files

`outbox-backpressure.tpn` | `properties.md` | `state-space.md`

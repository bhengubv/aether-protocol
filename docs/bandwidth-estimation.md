# AetherNet Bandwidth Measurement Framework (ABMF)

**Status:** Stable since AetherNet 1.6.0  
**Owner:** The Other Bhengu (Pty) Ltd t/a The Geek Network  
**Standards:** RFC 6298, RFC 5136, RFC 9002, BBRv3, RFC 8836/GCC

---

## 1. Problem statement

AetherNet nodes run over heterogeneous radio transports (BLE, Wi-Fi Direct, NearLink, LoRa, HTTP relay). Before ABMF:

- `ITransportService.MaxBandwidthBps` was a static constant — the theoretical physical-layer ceiling, not a measured value.
- `IStreamingService.UpdateBandwidthEstimate()` required callers to supply a value; nothing computed it.
- `PerTransportMetrics.EwmaThroughputBps` was an EWMA of `bytes / RTT` — confounds throughput with RTT; returns 0 when the link is idle.
- No mechanism produced estimates before the first packet was sent.
- No mechanism measured bandwidth across multiple transports simultaneously.

---

## 2. Architecture

```
UI / App layer
  └─ INodeActivityMonitor          ← observable state + rates for status bars, dashboards
       └─ IBandwidthDirector       ← cross-transport synthesis + gossip coordination
            └─ IBandwidthEstimator ← per-transport BBRv3 state machine
                 └─ Passive observations (RecordDelivery) +
                    Active probes (BandwidthProbe / BandwidthAck packets)
```

---

## 3. Per-transport estimation: BBRv3

`BandwidthEstimator` implements a BBRv3-inspired state machine per transport.

### 3.1 BtlBw (bottleneck bandwidth)

BBRv3 §4.3.2.1. Rolling **maximum** delivery rate over a window of `10 × RTprop` seconds.

```
BtlBw = max { deliveryRate_i | i ∈ last N samples, age(i) < 10 × RTprop }

where deliveryRate = bytes_delivered × 8 / elapsed_time
```

Using the **maximum** (not average) ensures we measure the network's capacity, not the current utilisation. Average would track load, not the pipe.

### 3.2 RTprop (round-trip propagation delay)

BBRv3 §4.3.2.2. Rolling **minimum** RTT observed in the last 10 seconds.

```
RTprop = min { RTT_i | i ∈ last 10 seconds }
```

The minimum filters out queueing delay (which inflates RTT when the pipe is congested). Periodically the estimator enters a ProbeRTT phase — reduces in-flight bytes to flush the queue and get a clean RTprop sample.

### 3.3 RFC 6298 SRTT and RTTVAR

```
First sample:
  SRTT    = R
  RTTVAR  = R / 2

Subsequent samples:
  RTTVAR  = (1 − 1/4) × RTTVAR + (1/4) × |SRTT − R|
  SRTT    = (1 − 1/8) × SRTT   + (1/8) × R

RTO = SRTT + max(1 ms, 4 × RTTVAR)
RTO is clamped to [200 ms, 60 s] per RFC 6298 §2.4.
```

### 3.4 Bandwidth-Delay Product

```
BDP (bytes) = BtlBw (bps) × RTprop (s) / 8
```

BDP is the optimal in-flight window size — the amount of data that fills the pipe exactly. Sending more causes queuing; sending less under-utilises the link.

### 3.5 Loss rate

EWMA with α = 0.10. Loss events come from `RecordLoss()`. Successful deliveries feed a 0-loss observation into the same EWMA.

```
LossRate = α × observation + (1 − α) × LossRate
```

### 3.6 PHY-layer capping

BLE RSSI → BtlBw cap (IEEE 802.11 / Bluetooth SIG Core Spec 5.4):

| RSSI (dBm) | BtlBw cap |
|---|---|
| ≥ −50 | 600 Mbps |
| ≥ −67 | 200 Mbps |
| ≥ −70 | 2 Mbps (BLE 2Msym/s PHY) |
| ≥ −80 | 54 Mbps |
| ≥ −85 | 500 kbps |
| ≥ −95 | 125 kbps |
| < −95 | 40 kbps (marginal link) |

The effective bandwidth = min(BtlBw, PhyCap). This prevents an optimistic BtlBw estimate from causing streaming to over-commit on a deteriorating radio link before probe data catches up.

### 3.7 Confidence tiers

| Tier | Condition | Use |
|---|---|---|
| None | No probes, no gossip | Use conservative fallback |
| Low | 1–4 rounds | ABR safe, but use lower bitrate rung |
| Medium | 5–19 rounds | 90 % CI — normal ABR decisions |
| High | ≥ 20 rounds | 95 % CI — scheduling, routing, SLA |

---

## 4. Active probing

Wire protocol additions (packet types 53–55):

### BandwidthProbe (type 53)
Sender → Receiver. Carries a 32-bit sequence number and a 64-bit send timestamp (µs since epoch). Padded to a configurable size (default 64 bytes) so the probe exercises the PHY framing overhead.

### BandwidthAck (type 54)
Receiver → Sender. Echoes the send timestamp plus adds receive and ack-send timestamps. The four timestamps allow clock-sync-free RTT computation:

```
RTT = (SenderReceive − SenderSend) − (ReceiverSend − ReceiverReceive)
         ──────────────── round-trip ────────   ─── receiver processing ───
```

Forward OWD (sender→receiver) can also be derived, but requires clock synchronisation — use with caution.

### Probe pacing

Probes are paced to < 0.5 % overhead of the estimated BDP. This mirrors QUIC's probe-at-1.25×BDP rule (RFC 9002 §7.7). Probes are only sent when the link is idle (no application traffic in the last 500 ms). This means probes never compete with data.

---

## 5. Cross-transport director

`BandwidthDirector` maintains a `(peerUhid × transportName) → BandwidthSample` matrix.

### Transport selection algorithm

```
score(transport) = (AvailableBps / PowerCost) × bdpBonus × confidenceFactor

where:
  PowerCost       = per-transport constant (NearLink=1, BLE=2, Wi-Fi Direct=3, relay=10)
  bdpBonus        = 1.5 if payloadBytes ≤ BDP, else 1.0
  confidenceFactor = 0.5 if Confidence==None, else 1.0
```

The `bdpBonus` means: for small payloads that fit in BDP, a lower-power transport with smaller BDP still wins over a high-bandwidth transport whose BDP requires multiple round-trips to fill.

---

## 6. Gossip warm-start

**This has no equivalent in TCP, QUIC, GCC, or BBRv3.** It is a novel AetherNet invention.

When two nodes complete a handshake (`Hello` / `HelloAck`), each node emits a `BandwidthGossip` packet (type 55) carrying its current BtlBw estimate. The receiving node feeds this into its `BandwidthEstimator` via `WarmFromGossip()`.

Effect: a new session starts with a non-zero, topology-informed estimate instead of the cold-start value of ~14.6 kB/s (RFC 6928 §2). For streaming, this means ABR can select the correct bitrate rung immediately rather than spending the first 8–10 RTTs discovering capacity.

Gossip is only accepted when the local estimator has `Confidence == None`. It never downgrades an existing estimate.

---

## 7. Node activity monitor

`INodeActivityMonitor` is the **UI-facing layer**. It produces `NodeActivitySnapshot` at a configurable cadence (default 500 ms) from byte counters fed by the transport layer.

```
NodeActivityState:
  Offline  → no transports available
  Idle     → transports available, no data in last IdleThresholdSeconds
  Active   → data flowing, utilization < 50%
  Busy     → utilization ≥ 50%
  Degraded → loss rate > 5% or delivery rate declining
```

### Consumption patterns

| Consumer | Pattern |
|---|---|
| App status bar | Poll `INodeActivityMonitor.Current` every 1 s |
| Blazor / reactive UI | Subscribe to `INodeActivityMonitor.SnapshotChanged` |
| BigBruh SignalR dashboard | Subscribe to `SnapshotChanged`, push to hub |
| ABR controller | Subscribe, step down bitrate ladder on `Degraded` |

`Activity` (IObservable) emits every tick (unconditional heartbeat). `SnapshotChanged` only fires when state, rates, or transport count changes.

---

## 8. What sets ABMF apart from existing standards

| Feature | TCP/QUIC | BBRv3 | GCC/SCReAM | **ABMF** |
|---|---|---|---|---|
| Measures real BtlBw | ✅ | ✅ | ✅ | ✅ |
| Cross-transport comparison | ❌ | ❌ | ❌ | ✅ |
| Gossip warm-start | ❌ | ❌ | ❌ | ✅ |
| PHY-layer RSSI capping | ❌ | ❌ | ❌ | ✅ |
| UI-surfaceable activity state | ❌ | ❌ | ❌ | ✅ |
| Confidence tiers | ❌ | ❌ | ❌ | ✅ |
| Formal convergence proof | ❌ | ❌ | ❌ | ✅ (Petri net) |
| Cold-start from zero | ✅ | ✅ | ✅ | ❌ (gossip warms) |

---

## 9. Formal convergence model

`formal/bandwidth-convergence.pnml` contains a timed Petri net proving:

**Theorem:** For any link with true bottleneck bandwidth `B` and true RTprop `R`, the ABMF estimator's BtlBw estimate converges to within 10% of `B` within `max(20, ⌈10 × R / probe_interval⌉)` probe rounds, assuming:
1. At least one probe per RTprop window.
2. Packet loss rate < 50%.
3. No topology change between rounds.

The proof proceeds by showing the BtlBw max-filter window eventually contains at least one sample from a round where no queueing inflated the delivery rate, and that sample converges to `B` by the law of large numbers applied to the delivery-rate estimator.

---

## 10. Reference implementation

- `src/AetherNet.Core/Bandwidth/` — interfaces and models
- `src/AetherNet.Transport/Bandwidth/` — `BandwidthEstimator`, `BandwidthDirector`, `NodeActivityMonitor`
- `tests/AetherNet.Core.Tests/Bandwidth/` — unit tests (BandwidthEstimatorTests, NodeActivityMonitorTests, BandwidthDirectorTests)
- `formal/bandwidth-convergence.pnml` — formal convergence proof

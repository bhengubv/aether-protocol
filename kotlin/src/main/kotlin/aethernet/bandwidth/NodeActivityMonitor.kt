// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import java.time.Instant
import java.util.Timer
import java.util.TimerTask
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicLong

/**
 * Observable node activity monitor — the UI-facing layer of the AetherNet
 * Bandwidth Measurement Framework.
 *
 * Produces [NodeActivitySnapshot] objects at a configurable cadence (default 500 ms).
 * Each snapshot aggregates per-transport ingress/egress rates, active peer counts,
 * and a unified [NodeActivityState] for status indicators.
 *
 * ## Consumption patterns
 * - **Status bar / widget (polling):** read [current] on a 1-second timer.
 * - **Reactive UI:** [subscribe] for push notifications on each sample.
 * - **ABR controller:** watch for [NodeActivityState.DEGRADED] and step down.
 *
 * Thread-safe: byte counters use [AtomicLong]; snapshot reference is [@Volatile].
 *
 * @param sampleIntervalMs How often the monitor re-samples (ms). Default 500.
 * @param idleThresholdSeconds How long without traffic before a transport is idle. Default 5.
 */
class NodeActivityMonitor(
    sampleIntervalMs: Int = 500,
    idleThresholdSeconds: Int = 5,
) {

    // ── Configuration ─────────────────────────────────────────────────────────

    @Volatile private var _sampleIntervalMs: Int = sampleIntervalMs.coerceIn(100, 60_000)
    var sampleIntervalMs: Int
        get() = _sampleIntervalMs
        set(value) { _sampleIntervalMs = value.coerceIn(100, 60_000) }

    @Volatile private var _idleThresholdSeconds: Int = idleThresholdSeconds.coerceIn(1, 300)
    var idleThresholdSeconds: Int
        get() = _idleThresholdSeconds
        set(value) { _idleThresholdSeconds = value.coerceIn(1, 300) }

    // ── Registered transports ─────────────────────────────────────────────────

    private val transports = ConcurrentHashMap<String, TransportEntry>()

    // ── Active-peer tracking ───────────────────────────────────────────────────
    // Maps peerUhid → last-seen Unix ms. A peer is "active" if it had ingress or
    // egress within idleThresholdSeconds. Populated only by the peer-aware
    // recordIngressFromPeer/recordEgressToPeer overloads; the transport-only
    // methods do not contribute (the caller did not supply a peer). Stale entries
    // are pruned each tick so the map stays bounded by the count of recently-active
    // peers, not the lifetime peer set.

    private val lastSeenPeerMs = ConcurrentHashMap<String, Long>()

    // ── Timer ─────────────────────────────────────────────────────────────────

    private var timer: Timer? = null
    private var lastTickMs = System.currentTimeMillis()

    // ── Snapshot ──────────────────────────────────────────────────────────────

    @Volatile private var _current: NodeActivitySnapshot = offlineSnapshot()

    /** The most recent snapshot. Thread-safe (volatile reference). */
    val current: NodeActivitySnapshot get() = _current

    // ── Subscribers ───────────────────────────────────────────────────────────

    private val subscribers = CopyOnWriteArrayList<(NodeActivitySnapshot) -> Unit>()

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /** Start the background sampling loop. */
    fun start() {
        lastTickMs = System.currentTimeMillis()
        val interval = sampleIntervalMs.toLong()
        timer = Timer("AetherNodeActivityMonitor", /* isDaemon = */ true).also {
            it.scheduleAtFixedRate(object : TimerTask() {
                override fun run() { onTick() }
            }, interval, interval)
        }
    }

    /** Stop the background sampling loop. */
    fun stop() {
        timer?.cancel()
        timer = null
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /**
     * Register a transport's estimator so its activity is included in snapshots.
     * The [name] must match the transport name used in [recordIngress]/[recordEgress].
     */
    fun register(name: String, estimator: BandwidthEstimator) {
        transports[name] = TransportEntry(estimator)
    }

    // ── Traffic recording ─────────────────────────────────────────────────────

    /** Record inbound bytes on a transport. Call from the transport receive path. */
    fun recordIngress(transport: String, bytes: Int) {
        transports[transport]?.ingressBytes?.addAndGet(bytes.toLong())
    }

    /** Record outbound bytes on a transport. Call from the transport send path. */
    fun recordEgress(transport: String, bytes: Int) {
        transports[transport]?.let {
            it.egressBytes.addAndGet(bytes.toLong())
            it.lastEgressMs.set(System.currentTimeMillis())
        }
    }

    /**
     * Record inbound bytes on a transport from a specific peer.
     * Tracks the peer for the [NodeActivitySnapshot.activePeers] count.
     */
    fun recordIngressFromPeer(transport: String, peerUhid: String, bytes: Int) {
        recordIngress(transport, bytes)
        if (peerUhid.isNotEmpty())
            lastSeenPeerMs[peerUhid] = System.currentTimeMillis()
    }

    /**
     * Record outbound bytes on a transport to a specific peer.
     * Tracks the peer for the [NodeActivitySnapshot.activePeers] count.
     */
    fun recordEgressToPeer(transport: String, peerUhid: String, bytes: Int) {
        recordEgress(transport, bytes)
        if (peerUhid.isNotEmpty())
            lastSeenPeerMs[peerUhid] = System.currentTimeMillis()
    }

    // ── Subscription ─────────────────────────────────────────────────────────

    /**
     * Subscribe to snapshot notifications.
     * Returns an unsubscribe function — call it to stop receiving updates.
     */
    fun subscribe(callback: (NodeActivitySnapshot) -> Unit): () -> Unit {
        subscribers.add(callback)
        return { subscribers.remove(callback) }
    }

    // ── Timer callback ────────────────────────────────────────────────────────

    private fun onTick() {
        val nowMs = System.currentTimeMillis()
        val elapsedSec = maxOf(0.001, (nowMs - lastTickMs) / 1000.0)
        lastTickMs = nowMs

        val transportSnapshots = mutableListOf<TransportActivitySnapshot>()
        var totalIngress = 0L
        var totalEgress = 0L
        var activeTransports = 0
        val idleThresholdMs = idleThresholdSeconds * 1000L

        // Count distinct peers active within the idle window; prune stale entries
        // so the map stays bounded by recently-active peers.
        var activePeers = 0
        val peerIterator = lastSeenPeerMs.entries.iterator()
        while (peerIterator.hasNext()) {
            val lastSeen = peerIterator.next().value
            if (nowMs - lastSeen < idleThresholdMs) activePeers++
            else peerIterator.remove()
        }

        for ((name, entry) in transports) {
            val ingressDelta = entry.ingressBytes.getAndSet(0L)
            val egressDelta  = entry.egressBytes.getAndSet(0L)

            val ingressBps = (ingressDelta * 8.0 / elapsedSec).toLong()
            val egressBps  = (egressDelta  * 8.0 / elapsedSec).toLong()

            val sample = entry.estimator.currentSample
            val utilFraction = if (sample.btlBwBps > 0)
                (egressBps.toDouble() / sample.btlBwBps).coerceIn(0.0, 1.0)
            else 0.0

            val isRecent = (nowMs - entry.lastEgressMs.get()) < idleThresholdMs
            val state = computeTransportState(egressBps, ingressBps, sample, isRecent)

            if (state != NodeActivityState.OFFLINE && state != NodeActivityState.IDLE)
                activeTransports++

            totalIngress += ingressBps
            totalEgress  += egressBps

            transportSnapshots.add(
                TransportActivitySnapshot(
                    transportName = name,
                    isAvailable = true,
                    ingressBps = ingressBps,
                    egressBps = egressBps,
                    srtt = sample.srtt,
                    btlBwBps = sample.btlBwBps,
                    utilizationFraction = utilFraction,
                    state = state,
                    confidence = sample.confidence,
                )
            )
        }

        val nodeState = computeNodeState(transportSnapshots)
        val primary = transportSnapshots.maxByOrNull { it.egressBps }
            ?.takeIf { nodeState != NodeActivityState.OFFLINE && nodeState != NodeActivityState.IDLE }
            ?.transportName

        val snapshot = NodeActivitySnapshot(
            state = nodeState,
            ingressBps = totalIngress,
            egressBps = totalEgress,
            activePeers = activePeers,
            activeTransports = activeTransports,
            transports = transportSnapshots,
            primaryTransportName = primary,
            timestamp = Instant.now(),
        )

        val prev = _current
        _current = snapshot

        // Notify all subscribers (heartbeat + change-only guard for efficiency).
        if (snapshot.state != prev.state ||
            Math.abs(snapshot.totalBps - prev.totalBps) > 1_000 ||
            snapshot.activeTransports != prev.activeTransports) {
            subscribers.forEach { cb -> try { cb(snapshot) } catch (_: Exception) {} }
        }
    }

    // ── State computation ─────────────────────────────────────────────────────

    private fun computeTransportState(
        egressBps: Long,
        ingressBps: Long,
        sample: BandwidthSample,
        isRecent: Boolean,
    ): NodeActivityState {
        if (egressBps == 0L && ingressBps == 0L) return NodeActivityState.IDLE
        if (!isRecent) return NodeActivityState.IDLE

        if (sample.lossRate > 0.05) return NodeActivityState.DEGRADED

        val util = if (sample.btlBwBps > 0)
            egressBps.toDouble() / sample.btlBwBps
        else 0.0

        return if (util >= 0.5) NodeActivityState.BUSY else NodeActivityState.ACTIVE
    }

    private fun computeNodeState(transports: List<TransportActivitySnapshot>): NodeActivityState {
        if (transports.isEmpty()) return NodeActivityState.OFFLINE
        if (transports.any { it.state == NodeActivityState.DEGRADED }) return NodeActivityState.DEGRADED
        if (transports.any { it.state == NodeActivityState.BUSY })     return NodeActivityState.BUSY
        if (transports.any { it.state == NodeActivityState.ACTIVE })   return NodeActivityState.ACTIVE
        if (transports.all { it.state == NodeActivityState.OFFLINE })  return NodeActivityState.OFFLINE
        return NodeActivityState.IDLE
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private fun offlineSnapshot(): NodeActivitySnapshot =
        NodeActivitySnapshot(
            state = NodeActivityState.OFFLINE,
            ingressBps = 0L,
            egressBps = 0L,
            activePeers = 0,
            activeTransports = 0,
            transports = emptyList(),
            primaryTransportName = null,
            timestamp = Instant.now(),
        )

    // ── Inner types ───────────────────────────────────────────────────────────

    private class TransportEntry(val estimator: BandwidthEstimator) {
        val ingressBytes = AtomicLong(0L)
        val egressBytes  = AtomicLong(0L)
        val lastEgressMs = AtomicLong(System.currentTimeMillis())
    }
}

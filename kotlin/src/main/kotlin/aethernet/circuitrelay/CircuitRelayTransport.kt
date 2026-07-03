// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import aethernet.protocol.MeshPacket
import aethernet.transport.PerTransportMetrics
import aethernet.transport.TransportService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Coroutine [TransportService] adapter over the native circuit-relay-v2 engine ([Transport]).
 *
 * The engine is deliberately coroutine-free (blocking [java.util.concurrent.ArrayBlockingQueue]
 * handoffs, a single [java.util.concurrent.locks.ReentrantLock]) so it compiles under AOSP Soong,
 * which runs plain `kotlinc` without the kotlinx-coroutines integration the rest of the JVM build
 * relies on. This adapter — and ONLY this adapter — bridges that blocking engine to the
 * `suspend`/[Flow] [TransportService] contract that [aethernet.transport.TransportManager] and the
 * [aethernet.transport.PredictiveTransportSelector] consume:
 *
 *  - [sendAsync] runs the engine's blocking [Transport.send] on [Dispatchers.IO], so a CONNECT/RESERVE
 *    wait never parks a shared coroutine-dispatcher thread.
 *  - [dataReceived] is a [MutableSharedFlow] fed by the engine's `setOnData` callback (invoked from the
 *    host's mesh-delivery thread), mirroring [aethernet.transport.InProcessTransport]. `replay = 0` with
 *    a bounded `extraBufferCapacity` matches that transport; `tryEmit` keeps the engine's non-suspend
 *    callback non-blocking.
 *
 * Mirrors the C# `CircuitRelayTransportService : ITransportService` — identical [name] and
 * [powerCostRelative] (90, just below the HTTP relay's last-resort 100) so
 * [aethernet.transport.TransportManager] auto-selects it last, exactly like the C# `TransportManager`
 * fall-through. kotlinx.coroutines stays OUT of the engine files; it lives here alone.
 *
 * @param localUhid this node's UHID.
 * @param link one-hop link to directly-reachable nodes (the [MeshRelayLink] in production).
 * @param options relay policy/tuning.
 * @param ioContext dispatcher the blocking engine send runs on (injectable for tests; defaults to IO).
 */
class CircuitRelayTransport(
    private val localUhid: String,
    private val link: RelayLink,
    options: RelayOptions = RelayOptions(),
    private val ioContext: kotlin.coroutines.CoroutineContext = Dispatchers.IO,
) : TransportService {

    /** The wrapped native engine. Blocking, coroutine-free — never leaks out of this adapter. */
    private val engine: Transport = Transport(localUhid, link, options)

    /**
     * Mirrors the engine's own disposed flag from the adapter side (the engine keeps its flag
     * private, and we touch none of its serialization/state code). Set by [close]; drives
     * [isAvailable] so a closed relay drops out of [aethernet.transport.TransportManager] selection.
     */
    private val closed = AtomicBoolean(false)

    // SharedFlow(replay = 0, buffered) fed by the engine's onData callback — same shape as
    // InProcessTransport so subscribers observe (senderUhid, payload) tuples off the delivery thread.
    private val mutableDataReceived = MutableSharedFlow<Pair<String, ByteArray>>(
        replay = 0,
        extraBufferCapacity = 100,
    )

    init {
        // The engine invokes this from the host's (non-coroutine) mesh-delivery thread when a DATA
        // frame is delivered to us as the final endpoint. tryEmit stays non-blocking for that caller.
        engine.setOnData { sender, payload ->
            mutableDataReceived.tryEmit(sender to payload)
        }
    }

    // ── TransportService ──────────────────────────────────────────────────────

    override val name: String = "Circuit Relay (v2)"

    override val isAvailable: Boolean get() = !closed.get()

    /** Relayed path; conservatively below a direct link (mirrors the C# 5 Mbps). */
    override val maxBandwidthBps: Long = 5_000_000L

    /** Internet-scope; no physical range bound. */
    override val maxRangeMeters: Int = 0

    /**
     * Relayed traffic is costly (an extra hop through a third node), so it sits just below the HTTP
     * relay's last-resort cost of 100 — [aethernet.transport.TransportManager] picks it last.
     */
    override val powerCostRelative: Int = 90

    override val maxConcurrentPeers: Int = 256

    override val metrics: PerTransportMetrics = PerTransportMetrics()

    override val dataReceived: Flow<Pair<String, ByteArray>> = mutableDataReceived.asSharedFlow()

    override suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean =
        withContext(ioContext) { engine.send(peerUhid, data) }

    override suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean =
        sendAsync(peerUhid, data)

    override fun isConnected(peerUhid: String): Boolean = engine.isConnected(peerUhid)

    override fun close() {
        closed.set(true)
        engine.dispose()
    }

    // ── Relay / target API (surfaces the engine's roles; not part of TransportService) ──

    /**
     * Reserves capacity on [relayUhid] so peers can reach this node through it. Suspending wrapper
     * over the engine's blocking reserve; returns `true` once the relay confirms.
     */
    suspend fun reserveAsync(relayUhid: String): Boolean =
        withContext(ioContext) { engine.reserve(relayUhid) }

    /** Records that [destUhid] is reachable via relay [relayUhid] (directory/gossip in production). */
    fun setRoute(destUhid: String, relayUhid: String) = engine.setRoute(destUhid, relayUhid)

    /** Number of bridges this node is currently servicing as a relay (diagnostics/tests). */
    val activeBridgeCount: Int get() = engine.activeBridgeCount()

    /** Number of reservations this node is currently holding as a relay (diagnostics/tests). */
    val activeReservationCount: Int get() = engine.activeReservationCount()
}

/**
 * Wires a [CircuitRelayTransport] onto a [MeshRelayLink] — the Kotlin mirror of the C#
 * `MeshCircuitRelay.Create`. The host:
 *  1. registers the returned [CircuitRelayTransport] with the mesh — [aethernet.transport.TransportManager]
 *     includes it automatically via its `additionalTransports` constructor parameter, at
 *     [CircuitRelayTransport.powerCostRelative] 90 (just below the HTTP relay); and
 *  2. routes every received [aethernet.protocol.PacketType.CircuitRelayControl] packet to the returned
 *     link's [MeshRelayLink.handleIncomingPacket].
 */
object MeshCircuitRelay {
    /** A relay transport paired with the mesh link that carries its frames one hop. */
    data class RelayPair(val transport: CircuitRelayTransport, val link: MeshRelayLink)

    /**
     * Creates the relay transport + its mesh link.
     *
     * @param localUhid this node's UHID (stamped as each packet's source).
     * @param sendOneHop sends a [MeshPacket] to a directly-connected peer; `true` if handed off.
     * @param canReach reports whether this node has a direct one-hop link to a peer.
     * @param options relay policy/tuning.
     */
    fun create(
        localUhid: String,
        sendOneHop: (MeshPacket) -> Boolean,
        canReach: (String) -> Boolean,
        options: RelayOptions = RelayOptions(),
    ): RelayPair {
        val link = MeshRelayLink(localUhid, sendOneHop, canReach)
        val transport = CircuitRelayTransport(localUhid, link, options)
        return RelayPair(transport, link)
    }
}

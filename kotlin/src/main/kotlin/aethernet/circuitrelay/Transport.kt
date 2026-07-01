// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * The one-hop link a [Transport] uses to exchange raw relay frames with
 * *directly-reachable* nodes — the seam between circuit-relay-v2 (transport-agnostic)
 * and whatever real transport carries a frame one hop (BLE, Wi-Fi Direct, WebRTC,
 * the HTTP relay, or an in-process link in tests). Mirrors the C# `IRelayLink`
 * and the Go `RelayLink`.
 */
interface RelayLink {
    /**
     * Sends a raw relay frame to a node reachable in one hop. Returns `true` if the
     * frame was handed to that node's link.
     */
    fun sendFrame(node: String, frame: ByteArray): Boolean

    /** Whether this node currently has a direct one-hop link to [node]. */
    fun canReach(node: String): Boolean

    /**
     * Registers the handler invoked when a raw frame arrives from a directly-reachable
     * node (sending node's UHID, frame bytes).
     */
    fun onFrame(handler: (String, ByteArray) -> Unit)
}

/**
 * Tuning + policy for a [Transport] (mirrors C# `CircuitRelayOptions` and Go `Options`).
 * Durations are in milliseconds to match the injectable epoch-ms clock.
 */
data class RelayOptions(
    /** How long a granted reservation remains valid, in milliseconds. */
    val reservationTtlMs: Long = 30L * 60L * 1000L,
    /** Maximum concurrent reservations this node will hold as a relay. */
    val maxReservations: Int = 128,
    /** Maximum concurrent bridges this node will service as a relay. */
    val maxBridges: Int = 128,
    /** Per-bridge data budget in bytes granted by this relay. 0 = unlimited. */
    val bridgeDataLimitBytes: Long = 0,
    /** Per-bridge duration budget in seconds granted by this relay. 0 = unlimited. */
    val bridgeDurationLimitSeconds: Int = 0,
    /** How long a client waits for a CONNECT to be confirmed, in milliseconds. */
    val connectTimeoutMs: Long = 10L * 1000L,
    /** How long a client waits for a RESERVE to be confirmed, in milliseconds. */
    val reserveTimeoutMs: Long = 10L * 1000L,
    /** Whether this node grants reservations and bridges traffic for others. */
    val actAsRelay: Boolean = true
)

/**
 * Native circuit-relay-v2 transport engine. Any AetherNet node can act as a relay: a
 * node that cannot reach a peer directly routes through a third node that can reach
 * both. This is the decentralised, no-libp2p equivalent of libp2p's circuit-relay-v2.
 *
 * Three roles live in this one engine (a node can be any/all at once):
 *  - **Target** — [reserve] capacity on a relay so peers behind NAT can reach it.
 *  - **Client** — [send] to a peer for which a relay route is known ([setRoute]);
 *    performs the CONNECT handshake then tunnels DATA.
 *  - **Relay** — grants reservations, bridges CONNECT→STOP, and forwards DATA between
 *    the two legs under a data/duration budget.
 *
 * Frames are the native [RelayFrame] wire format (fixture-locked across all 8
 * languages). One hop of a frame is carried by the injected [RelayLink].
 *
 * Faithful port of the C# `CircuitRelayTransportService` and Go `Transport`. Uses a
 * single [ReentrantLock] over the state maps (matching the Go `sync.Mutex`) and
 * [ArrayBlockingQueue] handoffs for the CONNECT/RESERVE response waits — no
 * kotlinx.coroutines, so it stays AOSP-Soong-safe.
 *
 * @param localUhid this node's UHID.
 * @param link one-hop link to directly-reachable nodes.
 * @param options policy/tuning.
 * @param now injectable epoch-ms clock (deterministic reservation-expiry tests).
 * @param log optional line logger.
 */
class Transport(
    private val localUhid: String,
    private val link: RelayLink,
    private val options: RelayOptions = RelayOptions(),
    private val now: () -> Long = { System.currentTimeMillis() },
    private val log: ((String) -> Unit)? = null
) {
    // ── State (all guarded by [lock] except the concurrent pending maps) ──────
    private val lock = ReentrantLock()

    // Relay role
    private val reservations = HashMap<String, Long>()          // client UHID -> expiry (epoch ms)
    private val bridges = HashMap<UUID, RelayBridge>()          // connId -> bridge

    // Client / target role
    private val routes = HashMap<String, String>()             // dest -> relay
    private val peerBridges = HashMap<String, ActiveBridge>()  // peer -> bridge

    // Response handoffs. Kept in concurrent maps so a handler thread can find the
    // waiter's queue without contending on [lock] for the whole await.
    private val pendingConnects = ConcurrentHashMap<UUID, ArrayBlockingQueue<RelayStatus>>()
    private val pendingReservations = ConcurrentHashMap<String, ArrayBlockingQueue<RelayStatus>>()

    private val disposed = AtomicBoolean(false)

    /** Endpoint delivery callback: (senderUhid, payload). */
    private var onData: ((String, ByteArray) -> Unit)? = null

    init {
        link.onFrame(::onFrame)
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /**
     * Registers the callback invoked when tunnelled data is delivered to this node as
     * an endpoint (sender UHID, payload).
     */
    fun setOnData(cb: (String, ByteArray) -> Unit) {
        onData = cb
    }

    /**
     * Records that [dest] is reachable via [relay] (in production, from the directory /
     * reservation gossip; tests set it directly).
     */
    fun setRoute(dest: String, relay: String) = lock.withLock { routes[dest] = relay }

    /** Number of bridges this node is currently servicing as a relay (diagnostics/tests). */
    fun activeBridgeCount(): Int = lock.withLock { bridges.size }

    /** Number of reservations this node is currently holding as a relay (diagnostics/tests). */
    fun activeReservationCount(): Int = lock.withLock { reservations.size }

    /** True once a relay bridge to [peer] has been established. */
    fun isConnected(peer: String): Boolean = lock.withLock { peerBridges.containsKey(peer) }

    /**
     * Reserves capacity on [relay] so peers can reach this node through it. Returns
     * `true` once the relay confirms the reservation.
     */
    fun reserve(relay: String): Boolean {
        if (disposed.get()) return false
        if (!link.canReach(relay)) return false

        val q = ArrayBlockingQueue<RelayStatus>(1)
        pendingReservations[relay] = q
        try {
            val frame = RelayFrame(type = RelayMessageType.Reserve, sourceUhid = localUhid, relayUhid = relay)
            link.sendFrame(relay, RelayFrame.serialize(frame))
            return await(q, options.reserveTimeoutMs) == RelayStatus.Ok
        } finally {
            pendingReservations.remove(relay)
        }
    }

    /** Delivers [data] to [peer], establishing a relay bridge first if needed. */
    fun send(peer: String, data: ByteArray): Boolean {
        if (disposed.get()) return false

        val existing = lock.withLock { peerBridges[peer] }
        if (existing != null) return sendData(existing, peer, data)

        // No bridge yet — establish one through the known relay for this peer.
        val relay = lock.withLock { routes[peer] }
        if (relay == null || !link.canReach(relay)) {
            log?.invoke("[relay] no reachable relay route to $peer")
            return false
        }

        val status = connect(peer, relay)
        if (status != RelayStatus.Ok) {
            log?.invoke("[relay] connect to $peer via $relay failed: $status")
            return false
        }

        val bridge = lock.withLock { peerBridges[peer] } ?: return false
        return sendData(bridge, peer, data)
    }

    // ── Client handshake ──────────────────────────────────────────────────────

    private fun connect(dest: String, relay: String): RelayStatus {
        val connId = UUID.randomUUID()
        val q = ArrayBlockingQueue<RelayStatus>(1)
        pendingConnects[connId] = q
        try {
            val frame = RelayFrame(
                type = RelayMessageType.Connect,
                sourceUhid = localUhid,
                destinationUhid = dest,
                relayUhid = relay,
                connectionId = connId
            )
            if (!link.sendFrame(relay, RelayFrame.serialize(frame))) return RelayStatus.ConnectionFailed
            return await(q, options.connectTimeoutMs)
        } finally {
            pendingConnects.remove(connId)
        }
    }

    private fun sendData(bridge: ActiveBridge, peer: String, data: ByteArray): Boolean {
        val frame = RelayFrame(
            type = RelayMessageType.Data,
            sourceUhid = localUhid,
            destinationUhid = peer,
            relayUhid = bridge.relay,
            connectionId = bridge.connId,
            payload = data
        )
        return link.sendFrame(bridge.relay, RelayFrame.serialize(frame))
    }

    /** Blocks up to [timeoutMs] for a status handoff; [RelayStatus.ConnectionFailed] on timeout. */
    private fun await(q: ArrayBlockingQueue<RelayStatus>, timeoutMs: Long): RelayStatus =
        q.poll(timeoutMs, TimeUnit.MILLISECONDS) ?: RelayStatus.ConnectionFailed

    // ── Inbound dispatch ──────────────────────────────────────────────────────

    private fun onFrame(from: String, frameBytes: ByteArray) {
        if (disposed.get()) return
        val f = try {
            RelayFrame.deserialize(frameBytes)
        } catch (ex: Exception) {
            log?.invoke("[relay] dropped malformed frame from $from: ${ex.message}")
            return
        }
        try {
            when (f.type) {
                RelayMessageType.Reserve -> handleReserve(from, f)
                RelayMessageType.ReserveResponse -> handleReserveResponse(from, f)
                RelayMessageType.Connect -> handleConnect(from, f)
                RelayMessageType.Stop -> handleStop(from, f)
                RelayMessageType.StopResponse -> handleStopResponse(from, f)
                RelayMessageType.ConnectResponse -> handleConnectResponse(from, f)
                RelayMessageType.Data -> handleData(from, f)
            }
        } catch (ex: Exception) {
            log?.invoke("[relay] handler error for ${f.type} from $from: ${ex.message}")
        }
    }

    // Relay: grant/refuse a reservation.
    private fun handleReserve(from: String, f: RelayFrame) {
        val expiry: Long = lock.withLock {
            if (!options.actAsRelay || reservations.size >= options.maxReservations) return@withLock -1L
            val exp = now() + options.reservationTtlMs
            reservations[f.sourceUhid] = exp
            exp
        }
        if (expiry < 0) {
            send(
                from,
                RelayFrame(
                    type = RelayMessageType.ReserveResponse,
                    sourceUhid = f.sourceUhid,
                    relayUhid = localUhid,
                    status = RelayStatus.ReservationRefused
                )
            )
            return
        }
        send(
            from,
            RelayFrame(
                type = RelayMessageType.ReserveResponse,
                sourceUhid = f.sourceUhid,
                relayUhid = localUhid,
                status = RelayStatus.Ok,
                reservationExpiresAtMs = expiry
            )
        )
    }

    // Client: reservation confirmed/denied.
    private fun handleReserveResponse(from: String, f: RelayFrame) {
        pendingReservations[from]?.offer(f.status)
    }

    // Relay: A wants B. Validate B's reservation + reachability, open a STOP to B.
    private fun handleConnect(from: String, f: RelayFrame) {
        val a = f.sourceUhid
        val b = f.destinationUhid
        val connId = f.connectionId ?: return

        if (!options.actAsRelay) {
            replyConnect(a, f, RelayStatus.ConnectionFailed)
            return
        }

        // Validate + register the bridge under the lock, then act on the outcome.
        val outcome: ConnectOutcome = lock.withLock {
            val exp = reservations[b]
            if (exp == null || now() >= exp) {
                reservations.remove(b)
                return@withLock ConnectOutcome.Refuse(RelayStatus.NoReservation)
            }
            if (!link.canReach(b)) {
                return@withLock ConnectOutcome.Refuse(RelayStatus.ConnectionFailed)
            }
            if (bridges.size >= options.maxBridges) {
                return@withLock ConnectOutcome.Refuse(RelayStatus.ResourceLimitExceeded)
            }
            val deadline = if (options.bridgeDurationLimitSeconds > 0) {
                now() + options.bridgeDurationLimitSeconds.toLong() * 1000L
            } else {
                0L // no duration limit
            }
            bridges[connId] = RelayBridge(a, b, options.bridgeDataLimitBytes, deadline)
            ConnectOutcome.Bridged
        }

        when (outcome) {
            is ConnectOutcome.Refuse -> replyConnect(a, f, outcome.status)
            ConnectOutcome.Bridged -> send(
                b,
                RelayFrame(
                    type = RelayMessageType.Stop,
                    sourceUhid = a,
                    destinationUhid = b,
                    relayUhid = localUhid,
                    connectionId = connId,
                    limitDataBytes = options.bridgeDataLimitBytes,
                    limitDurationSeconds = options.bridgeDurationLimitSeconds
                )
            )
        }
    }

    // Target: relay says A wants us. Accept and record a return route to A.
    private fun handleStop(from: String, f: RelayFrame) {
        val connId = f.connectionId ?: return
        lock.withLock { peerBridges[f.sourceUhid] = ActiveBridge(connId, from) }
        send(
            from,
            RelayFrame(
                type = RelayMessageType.StopResponse,
                sourceUhid = f.sourceUhid,
                destinationUhid = localUhid,
                relayUhid = from,
                connectionId = connId,
                status = RelayStatus.Ok
            )
        )
    }

    // Relay: target accepted/refused. Finalise the bridge and answer the client.
    private fun handleStopResponse(from: String, f: RelayFrame) {
        val connId = f.connectionId ?: return

        val result: StopResult = lock.withLock {
            val br = bridges[connId] ?: return@withLock StopResult.Unknown
            if (f.status != RelayStatus.Ok) {
                bridges.remove(connId)
                return@withLock StopResult.Failed(br.a)
            }
            br.open = true
            StopResult.Opened(br.a, br.b, br.dataBudget)
        }

        when (result) {
            StopResult.Unknown -> return
            is StopResult.Failed -> replyConnect(result.aUhid, f, RelayStatus.ConnectionFailed)
            is StopResult.Opened -> send(
                result.aUhid,
                RelayFrame(
                    type = RelayMessageType.ConnectResponse,
                    sourceUhid = result.aUhid,
                    destinationUhid = result.bUhid,
                    relayUhid = localUhid,
                    connectionId = connId,
                    status = RelayStatus.Ok,
                    limitDataBytes = result.budget
                )
            )
        }
    }

    // Client: bridge established/refused.
    private fun handleConnectResponse(from: String, f: RelayFrame) {
        val connId = f.connectionId ?: return
        if (f.status == RelayStatus.Ok) {
            lock.withLock { peerBridges[f.destinationUhid] = ActiveBridge(connId, from) }
        }
        pendingConnects[connId]?.offer(f.status)
    }

    // Data: endpoint delivery, or relay forward (under budget).
    private fun handleData(from: String, f: RelayFrame) {
        if (f.destinationUhid == localUhid) {
            onData?.invoke(f.sourceUhid, f.payload)
            return
        }
        val connId = f.connectionId ?: return

        // Decide under the lock whether to forward; only forward outside it.
        val forward: Boolean = lock.withLock {
            val br = bridges[connId]
            if (br == null || !br.open || (from != br.a && from != br.b)) return@withLock false
            if (br.deadline != 0L && now() >= br.deadline) {
                bridges.remove(connId)
                return@withLock false
            }
            br.dataUsed += f.payload.size.toLong()
            if (br.dataBudget > 0 && br.dataUsed > br.dataBudget) {
                bridges.remove(connId)
                log?.invoke("[relay] bridge $connId exceeded data budget (${br.dataUsed}/${br.dataBudget})")
                return@withLock false
            }
            true
        }

        if (forward) {
            // Forward the frame unchanged to the other endpoint (= its dst).
            link.sendFrame(f.destinationUhid, RelayFrame.serialize(f))
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private fun send(to: String, f: RelayFrame) {
        link.sendFrame(to, RelayFrame.serialize(f))
    }

    private fun replyConnect(client: String, connect: RelayFrame, status: RelayStatus) {
        send(
            client,
            RelayFrame(
                type = RelayMessageType.ConnectResponse,
                sourceUhid = connect.sourceUhid,
                destinationUhid = connect.destinationUhid,
                relayUhid = localUhid,
                connectionId = connect.connectionId,
                status = status
            )
        )
    }

    /** Releases waiters and marks this engine dead. Idempotent. */
    fun dispose() {
        if (!disposed.compareAndSet(false, true)) return
        for (q in pendingConnects.values) q.offer(RelayStatus.ConnectionFailed)
        for (q in pendingReservations.values) q.offer(RelayStatus.ConnectionFailed)
    }

    // ── State records ────────────────────────────────────────────────────────

    /** A bridge this node is relaying. Mutated only under [lock]. */
    private class RelayBridge(
        val a: String,
        val b: String,
        val dataBudget: Long,
        val deadline: Long // 0 => no duration limit
    ) {
        var dataUsed: Long = 0
        var open: Boolean = false
    }

    /** An established bridge from this node's endpoint view: which connection, via which relay. */
    private data class ActiveBridge(val connId: UUID, val relay: String)

    private sealed class ConnectOutcome {
        object Bridged : ConnectOutcome()
        data class Refuse(val status: RelayStatus) : ConnectOutcome()
    }

    private sealed class StopResult {
        object Unknown : StopResult()
        data class Failed(val aUhid: String) : StopResult()
        data class Opened(val aUhid: String, val bUhid: String, val budget: Long) : StopResult()
    }
}

// SPDX-License-Identifier: MIT

package aethernet.transport

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import java.io.Closeable
import java.util.concurrent.atomic.AtomicLong

/**
 * Minimal multi-transport manager that routes a packet through the best available transport,
 * falling through to lower-priority transports until one succeeds or all fail.
 *
 * This is the Kotlin mirror of the C# `TransportManager`'s **additional-transports** path (its
 * step 6): the registered transports are ordered by [TransportService.powerCostRelative] ascending
 * and tried in turn, so a cost-90 [aethernet.circuitrelay.CircuitRelayTransport] is auto-selected
 * **last**, as the serverless fallback — a real selection, never a hand-wired call. (The C# manager
 * additionally hard-codes typed BLE / Wi-Fi Direct / NearLink / CircleLink slots ahead of the
 * additional list; the Kotlin transports are all plain [TransportService]s, so the single
 * power-cost-ordered list is the faithful, minimal equivalent.)
 *
 * Inbound data from every managed transport is re-surfaced through [dataReceived], tagged with the
 * winning transport's [TransportService.name] — mirroring the C# `DataReceived(sender, data, via)`
 * event, so a consumer can prove which transport delivered a message.
 *
 * @param transports the managed transports; order is irrelevant (re-sorted by power cost).
 * @param scope coroutine scope the per-transport inbound collectors run on (injectable for tests).
 */
class TransportManager(
    transports: Iterable<TransportService>,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob()),
) : Closeable {

    /** Managed transports, ascending by power cost — the relay (90) therefore sorts to the tail. */
    private val ordered: List<TransportService> =
        transports.sortedBy { it.powerCostRelative }

    private val mutableDataReceived = MutableSharedFlow<Triple<String, ByteArray, String>>(
        replay = 0,
        extraBufferCapacity = 100,
    )

    /** Flow of `(senderUhid, payload, viaTransportName)` from whichever transport delivered it. */
    val dataReceived: Flow<Triple<String, ByteArray, String>> = mutableDataReceived.asSharedFlow()

    private val _sendCount = AtomicLong(0)
    private val _failures = AtomicLong(0)

    /** Total successful sends across all managed transports. */
    val sendCount: Long get() = _sendCount.get()

    /** Total sends that exhausted every transport without success. */
    val failureCount: Long get() = _failures.get()

    init {
        // Fan every managed transport's inbound flow into the tagged manager flow. One collector per
        // transport, each re-emitting (sender, payload, transport.name) — the C# per-transport
        // DataReceived subscription, expressed as flow collection.
        for (t in ordered) {
            scope.launch {
                t.dataReceived.collect { (sender, payload) ->
                    mutableDataReceived.tryEmit(Triple(sender, payload, t.name))
                }
            }
        }
    }

    /**
     * Sends [data] to [peerUhid], trying each available transport in ascending power-cost order and
     * returning on the first success. The cost-90 circuit relay is the last resort. Returns `false`
     * only if every available transport declined.
     */
    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean {
        for (t in ordered) {
            if (!t.isAvailable) continue
            if (t.sendAsync(peerUhid, data)) {
                _sendCount.incrementAndGet()
                return true
            }
        }
        _failures.incrementAndGet()
        return false
    }

    /** Stream variant of [sendAsync]; same power-cost fall-through. */
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean {
        for (t in ordered) {
            if (!t.isAvailable) continue
            if (t.sendStreamAsync(peerUhid, data)) {
                _sendCount.incrementAndGet()
                return true
            }
        }
        _failures.incrementAndGet()
        return false
    }

    /** Cancels the inbound collectors. Managed transports are owned by the caller and not closed here. */
    override fun close() {
        scope.cancel()
    }
}

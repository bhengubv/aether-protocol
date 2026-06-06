// SPDX-License-Identifier: MIT

package aethermesh.transport

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import java.util.concurrent.ConcurrentHashMap

/**
 * In-memory transport for testing and demo purposes.
 *
 * Uses a companion object with a ConcurrentHashMap to route
 * messages between nodes in the same process.
 */
class InProcessTransport(override val name: String = "InProcess") : TransportService {
    override val isAvailable: Boolean = true
    override val maxBandwidthBps: Long = Long.MAX_VALUE
    override val maxRangeMeters: Int = Int.MAX_VALUE
    override val powerCostRelative: Int = 1
    override val maxConcurrentPeers: Int = Int.MAX_VALUE

    /** Non-null metrics instance; always present for in-process transport. */
    override val metrics: PerTransportMetrics = PerTransportMetrics()

    private val mutableDataReceived = MutableSharedFlow<Pair<String, ByteArray>>(
        replay = 0,
        extraBufferCapacity = 100
    )

    override val dataReceived: Flow<Pair<String, ByteArray>> = mutableDataReceived.asSharedFlow()

    private val connectedPeers = ConcurrentHashMap<String, Boolean>()

    override suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean {
        val startMs = System.currentTimeMillis()
        return try {
            val transport = Companion.transports[peerUhid]
            if (transport != null) {
                transport.mutableDataReceived.emit(Pair(name, data))
                val rttMs = (System.currentTimeMillis() - startMs).coerceAtLeast(1L)
                metrics.recordSample(rttMs, success = true, bytesTransferred = data.size.toLong())
                true
            } else {
                metrics.recordSample(0L, success = false, bytesTransferred = 0L)
                false
            }
        } catch (e: Exception) {
            metrics.recordSample(0L, success = false, bytesTransferred = 0L)
            false
        }
    }

    override suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean = sendAsync(peerUhid, data)

    override fun isConnected(peerUhid: String): Boolean = Companion.transports.containsKey(peerUhid)

    override fun close() {
        connectedPeers.clear()
    }

    companion object {
        private val transports = ConcurrentHashMap<String, InProcessTransport>()

        /**
         * Registers a transport with a given UHID.
         * Messages sent to this UHID will be delivered to the transport.
         */
        fun register(uhid: String, transport: InProcessTransport) {
            transports[uhid] = transport
        }

        /**
         * Unregisters a transport.
         */
        fun unregister(uhid: String) {
            transports.remove(uhid)
        }

        /**
         * Gets a registered transport by UHID.
         */
        fun getTransport(uhid: String): InProcessTransport? = transports[uhid]

        /**
         * Clears all registered transports.
         */
        fun clearAll() {
            transports.clear()
        }
    }
}

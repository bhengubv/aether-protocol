// SPDX-License-Identifier: MIT

package aether.transport

import kotlinx.coroutines.flow.Flow
import java.io.Closeable

/**
 * Abstraction for a physical mesh transport layer (BLE, Wi-Fi Direct, etc).
 *
 * Every transport implementation MUST expose this interface.
 * Aether is transport-agnostic: any physical communication channel that
 * can send and receive byte arrays between peers is a valid Aether transport.
 */
interface TransportService : Closeable {
    /**
     * Human-readable identifier (e.g., "BLE", "Wi-Fi Direct").
     */
    val name: String

    /**
     * Whether the transport is currently usable on this device.
     */
    val isAvailable: Boolean

    /**
     * Maximum throughput in bytes per second.
     */
    val maxBandwidthBps: Long

    /**
     * Maximum communication range in meters.
     */
    val maxRangeMeters: Int

    /**
     * Relative power consumption (1 = low, 10 = high).
     */
    val powerCostRelative: Int

    /**
     * Maximum simultaneous peer connections.
     */
    val maxConcurrentPeers: Int

    /**
     * Per-transport EWMA metrics for adaptive selection.
     * Implementations that do not track metrics may return null.
     */
    val metrics: PerTransportMetrics? get() = null

    /**
     * Sends a byte array to a specific peer.
     *
     * @param peerUhid Target peer UHID
     * @param data Byte array to send
     * @return True if send succeeded
     */
    suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean

    /**
     * Sends a stream to a peer (for large transfers, voice, video).
     *
     * @param peerUhid Target peer UHID
     * @param data Stream to send
     * @return True if send succeeded
     */
    suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean

    /**
     * Checks if a connection is active to a peer.
     *
     * @param peerUhid Target peer UHID
     * @return True if connected
     */
    fun isConnected(peerUhid: String): Boolean

    /**
     * Flow of received data from peers.
     * Emits (senderUhid, data) tuples.
     */
    val dataReceived: Flow<Pair<String, ByteArray>>

    /**
     * Closes the transport and releases resources.
     */
    override fun close()
}

// SPDX-License-Identifier: MIT

package aethermesh.extensibility

import aethermesh.models.DtnBundle
import aethermesh.models.SosAlert
import aethermesh.protocol.MeshPacket

/**
 * Records relays for reward calculation and decides whether a packet jumps the priority queue.
 * Default: no-op accounting; never prioritises.
 */
interface IncentiveProvider {
    suspend fun recordRelay(localUhid: String, packet: MeshPacket) {}
    suspend fun shouldPrioritize(packet: MeshPacket): Boolean = false
}

/**
 * Optional cloud-relay seam. Default returns false everywhere (offline-only mesh).
 */
interface BackendClient {
    suspend fun relayMessage(
        senderUhid: String,
        recipientUhid: String,
        encryptedContent: ByteArray,
        priority: Int
    ): Boolean = false

    suspend fun syncDtnBundle(bundle: DtnBundle): Boolean = false
    suspend fun syncSos(alert: SosAlert): Boolean = false
}

/**
 * Gates protocol features behind remote configuration. Default: every feature enabled.
 */
interface FeatureFlagProvider {
    suspend fun isEnabled(featureName: String): Boolean = true
}

/** No-op default implementations. */
class NoopIncentiveProvider : IncentiveProvider
class NoopBackendClient : BackendClient
class NoopFeatureFlagProvider : FeatureFlagProvider

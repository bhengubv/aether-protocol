// SPDX-License-Identifier: MIT

package aethernet.extensibility

import aethernet.models.DtnBundle
import aethernet.models.SosAlert
import aethernet.protocol.MeshPacket
import java.math.BigDecimal

/**
 * Records relays for reward calculation and decides whether a packet jumps the priority queue.
 * Default: no-op accounting; never prioritises.
 */
interface IncentiveProvider {
    suspend fun recordRelay(localUhid: String, packet: MeshPacket) {}
    suspend fun shouldPrioritize(packet: MeshPacket): Boolean = false

    /**
     * Called when the local user tips a content author. Distinct from
     * recordRelay (relay credit - paid to nodes that forward bytes); this
     * records direct creator -> consumer settlement (paid to the user who
     * AUTHORED the content). Host implementations (e.g. SDPKT, BhenguPay)
     * wire their settlement logic here. Default no-op does nothing.
     * Added in v1.2.0 - closes Issue #61 surfaced by Wave 16.
     */
    suspend fun recordCreatorTip(creatorUhid: String, amount: BigDecimal, contentHash: String) {}
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

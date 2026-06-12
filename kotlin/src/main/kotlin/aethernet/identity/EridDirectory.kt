// SPDX-License-Identifier: MIT

package aethernet.identity

/**
 * Resolves rotating [EphemeralRoutingId] (ERID) wire addresses to and from the stable peer
 * identities behind them — the piece that lets an ESTABLISHED relationship follow a peer's
 * rotating address while an outsider cannot.
 *
 * A node derives its OWN secret routingKey once (via [EphemeralRoutingId.deriveRoutingKey]) and
 * shares it with a peer INSIDE the established Signal session — never on the wire. Each side
 * stores the other's routingKey here, so either can compute the other's current ERID for
 * addressing and reverse-resolve an inbound ERID back to the peer it belongs to. An outsider
 * holds no routingKey and can do neither. Port of the C# reference
 * (src/AetherNet.Core/Identity/EridDirectory.cs).
 */
class EridDirectory(
    myRoutingKey: ByteArray,
    private val epochSeconds: Long = EphemeralRoutingId.DEFAULT_EPOCH_SECONDS,
    private val eridLength: Int = EphemeralRoutingId.DEFAULT_LENGTH,
) {
    private val myRoutingKey: ByteArray
    private val peerKeys = HashMap<String, ByteArray>()

    init {
        require(myRoutingKey.isNotEmpty()) { "myRoutingKey cannot be empty" }
        require(epochSeconds > 0) { "epochSeconds must be positive" }
        this.myRoutingKey = myRoutingKey.copyOf()
    }

    /** Our own current ERID for the epoch containing [unixSeconds]. */
    fun myErid(unixSeconds: Long): String =
        EphemeralRoutingId.derive(myRoutingKey, unixSeconds, epochSeconds, eridLength)

    /**
     * Store a peer's routingKey, learned inside an established session. Idempotent; a later call
     * replaces an earlier key for the same peer.
     *
     * @throws IllegalArgumentException if [peerUhid] or [peerRoutingKey] is empty.
     */
    fun rememberPeer(peerUhid: String, peerRoutingKey: ByteArray) {
        require(peerUhid.isNotEmpty()) { "peerUhid cannot be empty" }
        require(peerRoutingKey.isNotEmpty()) { "peerRoutingKey cannot be empty" }
        peerKeys[peerUhid] = peerRoutingKey.copyOf()
    }

    /** Forget a peer (session torn down / excommunicated). Returns false if unknown. */
    fun forgetPeer(peerUhid: String): Boolean = peerKeys.remove(peerUhid) != null

    /** The current ERID a known peer presents this epoch, or null if we hold no key for them. */
    fun eridForPeer(peerUhid: String, unixSeconds: Long): String? {
        val key = peerKeys[peerUhid] ?: return null
        return EphemeralRoutingId.derive(key, unixSeconds, epochSeconds, eridLength)
    }

    /**
     * Reverse-resolve an inbound wire ERID to the stable peer UHID behind it for the given epoch,
     * or null if no known peer currently presents it. O(n) over known peers — a node's actual
     * relationship count.
     */
    fun resolvePeer(erid: String, unixSeconds: Long): String? {
        if (erid.isEmpty()) return null
        for ((uhid, key) in peerKeys) {
            if (EphemeralRoutingId.derive(key, unixSeconds, epochSeconds, eridLength) == erid) {
                return uhid
            }
        }
        return null
    }

    /** Number of peers whose routingKey we currently hold. */
    val knownPeerCount: Int get() = peerKeys.size
}

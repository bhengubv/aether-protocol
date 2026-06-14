// SPDX-License-Identifier: MIT
//
// On-mesh Proof-of-Vicinity token exchange — the directed, two-key witness→subject co-presence proof,
// carried over PacketType.PoVTokenExchange (43). Kotlin port of
// AetherNet.Market.PoVTokenExchangeService, mirroring the Go port. Mirrors the AetherNet handler idiom
// established by MeshTipService (sign payload with the identity key → wrap in a signed MeshPacket →
// send) and ReputationGossipService (verify the enclosing packet against the supplied sender public
// key, which also enforces freshness + nonce replay-dedup).
//
// CRYPTO: signatures are real Ed25519 over the canonical token body (PoVToken.buildSignableTokenData =
// "SubjectUhid + TimestampTicks + Transport"), byte-identical to every other language implementation,
// so a token exchanged here interoperates on one mesh.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal. It attaches
// NO value semantics and never touches any money/reward layer.

package aethernet.market

import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import org.slf4j.LoggerFactory

/**
 * Issues and accepts on-mesh Proof-of-Vicinity tokens over [PacketType.PoVTokenExchange] (43).
 *
 * **Issue path:** refuse self-vouch / non-short-range → build a witness-signed [PoVToken] (real
 * Ed25519 over the canonical body, subject signature left empty) → serialise as snake_case JSON → wrap
 * in a signed point-to-point [MeshPacket] (type 43, TTL 1 — the subject is one short-range hop away) →
 * send to the subject.
 *
 * **Receive path:** verify the enclosing packet signature (freshness + nonce dedup) against the
 * supplied sender key → deserialise → reject self-echo / not-addressed-to-us / missing witness
 * signature → verify the witness's Ed25519 signature over the token body → counter-sign as the subject
 * with the local identity key → record the token (increment the witness's contribution to the local
 * node's score).
 */
class PoVTokenExchangeService(
    private val sender: MeshSender,
    private val signer: PacketSigner,
    private val identity: IdentitySigner,
) {
    private val lock = Any()
    private val tokensBySubject = HashMap<String, MutableList<PoVToken>>()

    /** Fires once a counter-signed token has been recorded locally. */
    var onTokenReceived: ((PoVToken) -> Unit)? = null

    // ── Injectable abstractions ───────────────────────────────────────────────

    /** Minimal mesh transport surface needed by [PoVTokenExchangeService]. */
    interface MeshSender {
        /** The UHID of the local node. */
        val localUhid: String

        /** Delivers [packet] toward [subjectUhid] (directed — one short-range hop). Returns true on success. */
        suspend fun send(packet: MeshPacket, subjectUhid: String): Boolean
    }

    /**
     * Signs and verifies the enclosing [MeshPacket] envelope. [verify] MUST also enforce freshness and
     * nonce replay-dedup (mirroring the C# IPacketSigningService), so a replayed or stale PoV exchange
     * is rejected before any crypto on the body.
     */
    interface PacketSigner {
        /** Populates [packet]'s envelope signature, nonce, and timestamp fields in place. */
        fun sign(packet: MeshPacket)

        /**
         * Verifies [packet]'s envelope signature against [senderPublicKey] AND enforces freshness +
         * replay-dedup. Returns true only for a fresh, correctly-signed, non-replayed packet.
         */
        fun verify(packet: MeshPacket, senderPublicKey: ByteArray): Boolean
    }

    /** Signs / verifies canonical token bodies with Ed25519 identity keys. */
    interface IdentitySigner {
        /** Produces a 64-byte Ed25519 signature over [data] using the local identity key. */
        fun signData(data: ByteArray): ByteArray

        /** Verifies [signature] over [data] against [publicKey]. */
        fun verifySignature(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /**
     * Mints a witness-signed PoV token for [subjectUhid] and sends it directed (TTL 1) over packet 43.
     * It refuses to mint over a non-short-range transport or to vouch for itself. Returns the token
     * that was issued (with an empty subject signature — the subject fills it on receipt), or null when
     * issuance was refused.
     */
    suspend fun issueToken(
        subjectUhid: String,
        transport: PoVTransportType = PoVTransportType.Ble,
    ): PoVToken? {
        if (subjectUhid.isEmpty()) {
            log.debug("PoV issue skipped — empty subject UHID")
            return null
        }

        // ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range channel.
        if (!transport.isShortRange()) {
            log.warn("PoV issue refused — transport {} is not short-range", transport)
            return null
        }

        val localUhid = sender.localUhid
        if (localUhid.isEmpty()) {
            log.debug("PoV issue skipped — local node not initialized")
            return null
        }

        // A node cannot vouch for itself — that would be a free, unbounded self-attestation.
        if (localUhid == subjectUhid) {
            log.warn("PoV issue refused — witness and subject are the same node")
            return null
        }

        val timestampTicks = PoVToken.unixMillisToTicks(System.currentTimeMillis())

        // Witness signs the canonical token body with the node's REAL Ed25519 identity key.
        val witnessSig = identity.signData(
            PoVToken.buildSignableTokenData(subjectUhid, timestampTicks, transport),
        )

        val token = PoVToken(
            witnessUhid = localUhid,
            subjectUhid = subjectUhid,
            timestampTicks = timestampTicks,
            transportUsed = transport,
            witnessSignature = witnessSig,
            subjectSignature = null, // filled by the subject when it counter-signs on receipt.
        )

        val packet = MeshPacket(
            type = PacketType.PoVTokenExchange,
            sourceUhid = localUhid,
            destinationUhid = subjectUhid, // directed — NOT a broadcast.
            ttl = 1, // co-present: the subject is one short-range hop away.
            payload = token.toJson().toByteArray(Charsets.UTF_8),
        )

        signer.sign(packet)
        val sent = sender.send(packet, subjectUhid)

        log.debug(
            "PoV token issued: witness={} subject={} transport={} sent={}",
            localUhid, subjectUhid, transport, sent,
        )
        return token
    }

    /**
     * Processes an inbound PoV exchange packet (type 43).
     *
     * Returns true when the token was accepted, counter-signed, and recorded. Returns false when the
     * packet should be silently discarded (wrong type, bad/stale/replayed envelope, malformed payload,
     * self-echo, not addressed to us, missing/invalid witness signature, witness == subject).
     */
    fun handleTokenExchange(packet: MeshPacket, senderPublicKey: ByteArray): Boolean {
        if (packet.type != PacketType.PoVTokenExchange) {
            log.debug("PoV exchange: unexpected packet type {} — ignored", packet.type)
            return false
        }

        // 1. Verify the enclosing MeshPacket signature (also enforces freshness + nonce replay-dedup).
        if (!signer.verify(packet, senderPublicKey)) {
            log.warn("PoV exchange from {}: packet signature invalid/stale/replayed — dropped", packet.sourceUhid)
            return false
        }

        // 2. Deserialise the token body.
        val token = PoVToken.fromJson(packet.payload.toString(Charsets.UTF_8))
        if (token == null || token.witnessUhid.isEmpty() || token.subjectUhid.isEmpty()) {
            log.warn("PoV exchange from {}: payload malformed or missing required fields — dropped", packet.sourceUhid)
            return false
        }

        // 3. The incoming token must already carry the witness's signature.
        val witnessSig = token.witnessSignature
        if (witnessSig == null || witnessSig.isEmpty()) {
            log.warn("PoV exchange from {}: token has no witness signature — dropped", token.witnessUhid)
            return false
        }

        val localUhid = sender.localUhid

        // 4. Ignore our own token echoed back to us (witness == us).
        if (localUhid.isNotEmpty() && token.witnessUhid == localUhid) {
            return false
        }

        // 5. The token must be addressed to us — we are the subject being vouched for.
        if (localUhid.isNotEmpty() && token.subjectUhid != localUhid) {
            log.debug("PoV exchange: token subject {} is not us — ignored", token.subjectUhid)
            return false
        }

        // 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the verified
        //    sender key (the witness is the packet source, so the envelope and the body share a
        //    signing key). A forged or tampered witness signature is rejected before we countersign.
        val signable = token.signableData()
        if (!identity.verifySignature(senderPublicKey, signable, witnessSig)) {
            log.warn("PoV exchange from {}: witness Ed25519 signature invalid — dropped", token.witnessUhid)
            return false
        }

        // 6b. A witness must not be vouching for itself — distinct parties is a hard PoV invariant.
        if (token.witnessUhid == token.subjectUhid) {
            log.warn("PoV exchange from {}: witness == subject — dropped", token.witnessUhid)
            return false
        }

        // 7. Counter-sign the SAME canonical body as the subject, with our REAL Ed25519 identity key.
        val subjectSig = identity.signData(signable)
        val accepted = token.copy(subjectSignature = subjectSig)

        // 8. Record it (increments the witness's contribution to OUR score) and notify.
        recordToken(accepted)
        onTokenReceived?.invoke(accepted)

        log.debug(
            "PoV token accepted: witness={} subject={} transport={}",
            accepted.witnessUhid, accepted.subjectUhid, accepted.transportUsed,
        )
        return true
    }

    /** Returns the local PoV trust score for [uhid], derived from recorded tokens. */
    fun getScore(uhid: String): PoVScore {
        val tokens: List<PoVToken>
        synchronized(lock) {
            tokens = tokensBySubject[uhid]?.toList() ?: emptyList()
        }

        val unique = tokens.map { it.witnessUhid }.distinct().size
        val weighted = if (unique > 0) unique / (unique + 1.0) else 0.0

        return PoVScore(
            uhid = uhid,
            uniqueWitnesses = unique,
            weightedScore = weighted,
            lastUpdatedUnixMs = System.currentTimeMillis(),
        )
    }

    /**
     * The sorted list of subject UHIDs with at least one recorded token. Mainly useful for tests and
     * diagnostics.
     */
    fun acceptedSubjects(): List<String> = synchronized(lock) {
        tokensBySubject.keys.sorted()
    }

    private fun recordToken(token: PoVToken) {
        synchronized(lock) {
            tokensBySubject.getOrPut(token.subjectUhid) { mutableListOf() }.add(token)
        }
    }

    private companion object {
        private val log = LoggerFactory.getLogger(PoVTokenExchangeService::class.java)
    }
}

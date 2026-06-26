// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity (PoV) anti-Sybil trust service (single-node, in-memory). Kotlin port of
// AetherNet.Market.IPoVService / InMemoryPoVService. Two users meet physically; their devices exchange
// a signed token over a short-range transport (BLE/NFC/NearLink). Over time a directed trust graph maps
// how many distinct humans have verified a profile.
//
// Signatures are REAL Ed25519 (Ed25519Service / BouncyCastle) over the canonical token body
// (PoVToken.buildSignableTokenData = "SubjectUhid + TimestampTicks + Transport"). The single-node
// service holds one identity key and produces both the witness and subject signatures with it; the
// two-party mesh exchange (each side counter-signs with its own key) is PoVTokenExchangeService.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal — it attaches
// NO value semantics and never touches any money/reward layer.

package aethernet.market

import aethernet.security.Ed25519Service
import java.util.concurrent.ConcurrentHashMap

/** The Proof-of-Vicinity trust service. */
interface PoVService {
    fun issueToken(witnessUhid: String, subjectUhid: String,
                   transport: PoVTransportType = PoVTransportType.Ble): PoVToken
    fun acceptToken(token: PoVToken)
    fun getScore(uhid: String): PoVScore
    fun verifyToken(token: PoVToken): Boolean
    fun reportDefection(witnessUhid: String, defectorUhid: String)
}

/** Single-node, in-memory [PoVService] for testing / single-node scenarios. */
class InMemoryPoVService : PoVService {
    private val tokensBySubject = ConcurrentHashMap<String, MutableList<PoVToken>>()
    private val scoreOverrides = ConcurrentHashMap<String, Double>()

    // Self-contained real Ed25519 identity; both signatures on a token it issues use this one key.
    private val keyPair = Ed25519Service.generateKeyPair()
    private val privateKey = keyPair.first
    private val publicKey = keyPair.second

    /** Fires when a token is issued or accepted. */
    var onTokenReceived: ((PoVToken) -> Unit)? = null

    override fun issueToken(witnessUhid: String, subjectUhid: String, transport: PoVTransportType): PoVToken {
        val ticks = PoVToken.unixMillisToTicks(System.currentTimeMillis())
        val signable = PoVToken.buildSignableTokenData(subjectUhid, ticks, transport)
        // REAL Ed25519 over the canonical body; both signatures from this node's one key (single-node).
        val sig = Ed25519Service.sign(privateKey, signable)
        val token = PoVToken(
            witnessUhid = witnessUhid,
            subjectUhid = subjectUhid,
            timestampTicks = ticks,
            transportUsed = transport,
            witnessSignature = sig,
            subjectSignature = sig,
        )
        onTokenReceived?.invoke(token)
        return token
    }

    override fun acceptToken(token: PoVToken) {
        // Record only a token that cryptographically verifies — both signatures valid + distinct parties.
        if (!verifyToken(token)) return
        tokensBySubject.getOrPut(token.subjectUhid) { mutableListOf() }.add(token)
        onTokenReceived?.invoke(token)
    }

    override fun getScore(uhid: String): PoVScore {
        val tokens = tokensBySubject[uhid]
        val override = scoreOverrides[uhid]

        if (tokens.isNullOrEmpty()) {
            // A UHID with no inbound tokens still surfaces a stored defection override.
            return PoVScore(uhid, 0, override ?: 0.0, System.currentTimeMillis())
        }

        val unique = tokens.map { it.witnessUhid }.toSet().size
        // Sigmoid-ish: w / (w + 1).
        var score = unique.toDouble() / (unique + 1.0)
        if (override != null) score = override
        return PoVScore(uhid, unique, score, System.currentTimeMillis())
    }

    override fun verifyToken(token: PoVToken): Boolean {
        val ws = token.witnessSignature
        val ss = token.subjectSignature
        // Structural: both parties signed, both UHIDs present, and distinct.
        if (ws == null || ws.isEmpty() || ss == null || ss.isEmpty() ||
            token.witnessUhid.isEmpty() || token.subjectUhid.isEmpty() ||
            token.witnessUhid == token.subjectUhid
        ) {
            return false
        }
        // Cryptographic: BOTH signatures valid over the canonical body.
        val signable = token.signableData()
        return Ed25519Service.verify(publicKey, signable, ws) &&
            Ed25519Service.verify(publicKey, signable, ss)
    }

    override fun reportDefection(witnessUhid: String, defectorUhid: String) {
        val score = getScore(witnessUhid)
        scoreOverrides[witnessUhid] = score.weightedScore * 0.8
    }
}

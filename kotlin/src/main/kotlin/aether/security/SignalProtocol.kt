// SPDX-License-Identifier: MIT

package aether.security

import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.generators.X25519KeyPairGenerator
import org.bouncycastle.crypto.params.X25519KeyGenerationParameters
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Signal Protocol implementation: X3DH session establishment + full
 * Double Ratchet (Signal §5).
 *
 * Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
 *   DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
 *   DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
 *   DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
 *   DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
 *
 * Initial root key: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
 *
 * Double Ratchet (§5): each side maintains a current X25519 ratchet keypair.
 * When the receiver sees a peer message bearing a new ratchet public key, it
 * does a DH-ratchet step: derive a new chain key via
 * KDF_RK(RK, DH(myDHs_priv, newDHr)), then generate a fresh DHs and derive its
 * sending chain via KDF_RK(RK, DH(newDHs_priv, newDHr)). Signal-canonical
 * X3DH↔DR integration: the initiator's X3DH ephemeral becomes its first
 * DH-ratchet keypair; the responder adopts the signed pre-key as its initial
 * DHs and rotates to a fresh keypair on its first DH-ratchet step.
 *
 * Symmetric ratchet (§5.1): HMAC-SHA256, single-byte domain separation
 *   (0x01 -> message key, 0x02 -> next chain key).
 * Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
 * Identity signing: Ed25519.
 */

/**
 * Wire-level encrypted payload.
 *
 * Two layered ratchets contribute fields:
 *  1. **X3DH session-establishment** (Signal §3) — populated only on the
 *     first message a new initiator sends to a peer (messageType=1):
 *     [initiatorIdentityKeyX25519], [usedSignedPreKeyId], [usedOneTimePreKeyId].
 *  2. **Double Ratchet** (Signal §5) — [senderEphemeralKeyX25519] and
 *     [previousChainCount] populated on EVERY message.
 *
 * [initiatorEphemeralKeyX25519] is retained for backward-compat with peers
 * still emitting the pre-Double-Ratchet wire envelope. New consumers should
 * read [senderEphemeralKeyX25519]; receivers fall back to
 * [initiatorEphemeralKeyX25519] when null.
 */
data class EncryptedPayload(
    val ciphertext: ByteArray,
    val nonce: ByteArray,
    /** 0 = normal, 1 = PreKey (initial). */
    val messageType: Int,
    val senderUhid: String,
    /** Message counter within the current sending chain (Signal §5: Ns). */
    val counter: Int,
    /** PreKey messages: initiator's long-term X25519 identity public key (32 bytes). */
    val initiatorIdentityKeyX25519: ByteArray? = null,
    /**
     * DEPRECATED: prefer [senderEphemeralKeyX25519]. On PreKey messages this
     * equals [senderEphemeralKeyX25519] (initiator's first DH-ratchet pub
     * IS its X3DH ephemeral); on normal messages it is null.
     */
    val initiatorEphemeralKeyX25519: ByteArray? = null,
    /** PreKey messages: SignedPreKeyId from the recipient bundle the initiator consumed. */
    val usedSignedPreKeyId: Int = 0,
    /** PreKey messages: one-time PreKeyId from the recipient bundle the initiator consumed. */
    val usedOneTimePreKeyId: Int = 0,
    /**
     * Sender's current DH-ratchet X25519 public key (32 bytes). Populated on
     * every message. Drives the DH-ratchet step on the receiver side: when
     * this changes, the receiver re-keys via KDF_RK(RK, DH(myDHs, newDHr)).
     */
    val senderEphemeralKeyX25519: ByteArray? = null,
    /**
     * Number of messages the sender sent in its previous sending chain
     * (Signal §5: PN). Used by the receiver to derive skipped message keys
     * when crossing a DH-ratchet boundary.
     */
    val previousChainCount: Int = 0,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EncryptedPayload) return false
        if (!ciphertext.contentEquals(other.ciphertext)) return false
        if (!nonce.contentEquals(other.nonce)) return false
        if (messageType != other.messageType) return false
        if (senderUhid != other.senderUhid) return false
        if (counter != other.counter) return false
        if (!(initiatorIdentityKeyX25519 contentEqualsNullable other.initiatorIdentityKeyX25519)) return false
        if (!(initiatorEphemeralKeyX25519 contentEqualsNullable other.initiatorEphemeralKeyX25519)) return false
        if (usedSignedPreKeyId != other.usedSignedPreKeyId) return false
        if (usedOneTimePreKeyId != other.usedOneTimePreKeyId) return false
        if (!(senderEphemeralKeyX25519 contentEqualsNullable other.senderEphemeralKeyX25519)) return false
        if (previousChainCount != other.previousChainCount) return false
        return true
    }

    override fun hashCode(): Int {
        var result = ciphertext.contentHashCode()
        result = 31 * result + nonce.contentHashCode()
        result = 31 * result + messageType
        result = 31 * result + senderUhid.hashCode()
        result = 31 * result + counter
        result = 31 * result + (initiatorIdentityKeyX25519?.contentHashCode() ?: 0)
        result = 31 * result + (initiatorEphemeralKeyX25519?.contentHashCode() ?: 0)
        result = 31 * result + usedSignedPreKeyId
        result = 31 * result + usedOneTimePreKeyId
        result = 31 * result + (senderEphemeralKeyX25519?.contentHashCode() ?: 0)
        result = 31 * result + previousChainCount
        return result
    }
}

/**
 * Pre-key bundle published by a node so others can initiate Signal sessions
 * toward it asynchronously.
 *
 * Two identity keys per node — Ed25519 for signing and X25519 for ECDH.
 */
data class PreKeyBundle(
    val uhid: String,
    /** Long-term Ed25519 identity public key (32 bytes). */
    val identityKey: ByteArray,
    /** Long-term X25519 identity public key (32 bytes raw, RFC 7748). */
    val identityKeyX25519: ByteArray,
    val preKeyId: Int,
    /** One-time pre-key X25519 public key (32 bytes raw). */
    val preKey: ByteArray,
    val signedPreKeyId: Int,
    /** Signed pre-key X25519 public key (32 bytes raw). */
    val signedPreKey: ByteArray,
    /** Ed25519 signature over signedPreKey (64 bytes). */
    val signedPreKeySignature: ByteArray,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PreKeyBundle) return false
        if (uhid != other.uhid) return false
        if (!identityKey.contentEquals(other.identityKey)) return false
        if (!identityKeyX25519.contentEquals(other.identityKeyX25519)) return false
        if (preKeyId != other.preKeyId) return false
        if (!preKey.contentEquals(other.preKey)) return false
        if (signedPreKeyId != other.signedPreKeyId) return false
        if (!signedPreKey.contentEquals(other.signedPreKey)) return false
        if (!signedPreKeySignature.contentEquals(other.signedPreKeySignature)) return false
        return true
    }

    override fun hashCode(): Int {
        var result = uhid.hashCode()
        result = 31 * result + identityKey.contentHashCode()
        result = 31 * result + identityKeyX25519.contentHashCode()
        result = 31 * result + preKeyId
        result = 31 * result + preKey.contentHashCode()
        result = 31 * result + signedPreKeyId
        result = 31 * result + signedPreKey.contentHashCode()
        result = 31 * result + signedPreKeySignature.contentHashCode()
        return result
    }
}

private infix fun ByteArray?.contentEqualsNullable(other: ByteArray?): Boolean {
    if (this == null && other == null) return true
    if (this == null || other == null) return false
    return this.contentEquals(other)
}

/**
 * Signal-Protocol session state with a single peer. Holds X3DH establishment
 * metadata plus full Double-Ratchet (Signal §5) state.
 *
 * Double-Ratchet state per §5:
 *  - [rootKey] (RK) — re-keyed on every DH-ratchet step.
 *  - [myEphemeralPriv]/[myEphemeralPub] (DHs) — my current ratchet keypair.
 *  - [remoteEphemeralPub] (DHr) — peer's last-known ratchet public key. Null
 *    until the first DH-ratchet step.
 *  - [sendChainKey] (CKs) — null until I've sent (or lazily initialised).
 *  - [recvChainKey] (CKr) — null until I've received on this chain.
 *  - [sendCounter]/[recvCounter] (Ns/Nr) — reset on each DH-ratchet step.
 *  - [previousChainCount] (PN) — messages I sent on my previous sending chain
 *    so the receiver can compute skipped keys across a DH-ratchet boundary.
 *  - [skippedMessageKeys] — keyed by "Hex(remoteEphPub):counter". The DHr
 *    binding is essential — out-of-order messages from a previous chain
 *    (different DHr) can still arrive after a DH-ratchet step, and they need
 *    their own per-chain key set.
 */
internal class SignalSession {
    var rootKey: ByteArray = ByteArray(0)
    /** Sending chain key. Null until first send (or until DH-ratchet rekeys it). */
    var sendChainKey: ByteArray? = null
    /** Receiving chain key. Null until first receive that triggers a DH-ratchet step. */
    var recvChainKey: ByteArray? = null

    var sendCounter: Int = 0
    var recvCounter: Int = 0
    /** Number of messages sent in the previous sending chain (Signal §5: PN). */
    var previousChainCount: Int = 0

    /** My current DH-ratchet private key (X25519, 32 bytes). */
    var myEphemeralPriv: ByteArray = ByteArray(0)
    /** My current DH-ratchet public key (X25519, 32 bytes). */
    var myEphemeralPub: ByteArray = ByteArray(0)
    /** Peer's last-seen DH-ratchet public key. Null until first DH-ratchet step. */
    var remoteEphemeralPub: ByteArray? = null

    /**
     * Skipped message keys keyed by "Hex(remoteEphPub):counter". The
     * remoteEphPub binding is essential for out-of-order messages that span
     * a DH-ratchet boundary.
     */
    val skippedMessageKeys: MutableMap<String, ByteArray> = mutableMapOf()

    /**
     * True iff this session was established in the initiator role and the
     * first outbound message has not yet been sent. While true, the next
     * encrypt() emits a PreKey message (messageType=1) carrying the X3DH
     * inputs.
     */
    var pendingPreKeyMessage: Boolean = false
    var initiatorIdentityKeyX25519: ByteArray = ByteArray(0)
    var usedSignedPreKeyId: Int = 0
    var usedOneTimePreKeyId: Int = 0
}

/**
 * Responder-side pre-key state — signed pre-key (rotated periodically) and a
 * pool of one-time pre-keys (each consumed exactly once).
 *
 * One-time pre-keys are managed as a pool of `opkPoolSize` (default 100)
 * entries. Bundle generation hands out the next-unused id from
 * [availableOpkIds]; the OPK stays in [oneTimePreKeys] until a responder
 * consumes it via X3DH, at which point it is zeroed and removed. Top-up runs
 * each time a bundle is generated so the available queue never empties under
 * steady load.
 */
internal class PreKeyStateInternal {
    var signedPreKeyId: Int = 0
    var signedPreKeyPriv: ByteArray = ByteArray(0)
    var signedPreKeyPub: ByteArray = ByteArray(0)
    var signedPreKeySignature: ByteArray = ByteArray(0)

    /** One-time pre-keys keyed by id. Removed and zeroed on consumption. */
    val oneTimePreKeys: MutableMap<Int, Pair<ByteArray, ByteArray>> = mutableMapOf()

    /**
     * IDs of OPKs that exist in [oneTimePreKeys] and have NOT yet been issued
     * in any bundle. Bundle generation pops from the front (FIFO). Top-up
     * generates new OPKs and enqueues them here.
     */
    val availableOpkIds: ArrayDeque<Int> = ArrayDeque()
}

class SignalProtocol(
    /**
     * Target size of the one-time pre-key pool. Defaults to 100 — mirrors
     * Signal's published guidance so realistic concurrent-initiator loads
     * don't collide on a single shared id. The pool is topped up to this
     * many available (un-issued) keys on every bundle generation, and
     * consumed keys are replaced lazily on the next bundle call.
     *
     * Pre-2026-05-05 the Kotlin module held only a single OPK at a time —
     * matching the (pre-pool) C# behaviour and creating the same concurrency
     * hazard: every concurrent initiator was handed an identical preKeyId and
     * the responder rejected all but the first. Switching to a pool fixes
     * that hazard for Kotlin too.
     */
    val opkPoolSize: Int = DEFAULT_OPK_POOL_SIZE,
) {
    init {
        require(opkPoolSize >= 1) { "opkPoolSize must be >= 1 (got $opkPoolSize)." }
    }

    companion object {
        /**
         * Maximum number of skipped message keys retained per session. If a
         * counter gap exceeds this, the session must be re-established.
         */
        const val MAX_SKIPPED_KEYS: Int = 1000

        const val MESSAGE_TYPE_NORMAL: Int = 0
        const val MESSAGE_TYPE_PRE_KEY: Int = 1

        /**
         * Default size of the one-time pre-key pool. Mirrors the C# reference
         * (`SignalProtocolService.DefaultOpkPoolSize`) — Signal's published
         * guidance is ~100 OPKs per device.
         */
        const val DEFAULT_OPK_POOL_SIZE: Int = 100

        private const val AES_KEY_SIZE = 32
        private const val AES_GCM_NONCE_SIZE = 12
        private const val AES_GCM_TAG_SIZE = 16
        private const val X25519_PUBLIC_KEY_SIZE = 32

        // HKDF info strings — these MUST match the C# reference exactly. Any
        // drift breaks cross-language interop.
        private val HKDF_ROOT_INFO = "aether-x3dh-root-v1".toByteArray(Charsets.UTF_8)

        /**
         * HKDF info string for the DH-ratchet step (Signal §5: KDF_RK). Each
         * step derives a 64-byte block: first 32 = new root key, second 32
         * = new chain key.
         */
        private val HKDF_RATCHET_INFO = "aether-ratchet-rk-v1".toByteArray(Charsets.UTF_8)

        private val rng = SecureRandom()

        // Domain-separation bytes for the symmetric ratchet (Signal §5.1).
        private val RATCHET_MESSAGE_KEY_INPUT = byteArrayOf(0x01)
        private val RATCHET_CHAIN_KEY_INPUT = byteArrayOf(0x02)

        private val HEX_CHARS = "0123456789ABCDEF".toCharArray()
    }

    private val sessions = ConcurrentHashMap<String, SignalSession>()

    // Long-term identity keys — two distinct keypairs per node.
    private val identityX25519Priv: ByteArray
    private val identityX25519Pub: ByteArray
    private val ed25519PrivateKey: ByteArray
    private val ed25519PublicKey: ByteArray

    private var localUhid: String? = null
    private val preKeys: PreKeyStateInternal = PreKeyStateInternal()

    /**
     * Lock guarding mutations of [preKeys]. Bundle generation, OPK top-up,
     * and OPK consumption (responder-side) all hold this lock for the whole
     * mutation so concurrent initiators never see partial state. Mirrors
     * `_preKeyLock` in the C# reference.
     */
    private val preKeyLock = Any()

    init {
        val (edPriv, edPub) = Ed25519Service.generateKeyPair()
        ed25519PrivateKey = edPriv
        ed25519PublicKey = edPub

        val (xPriv, xPub) = generateX25519KeyPair()
        identityX25519Priv = xPriv
        identityX25519Pub = xPub
    }

    /** Sets the local node's UHID. Required before any encrypt() call. */
    fun setLocalUhid(uhid: String) {
        require(uhid.isNotEmpty()) { "uhid cannot be empty" }
        localUhid = uhid
    }

    fun hasSession(peerUhid: String): Boolean = sessions.containsKey(peerUhid)

    fun encrypt(peerUhid: String, plaintext: ByteArray): EncryptedPayload {
        val session = sessions[peerUhid]
            ?: throw IllegalStateException("No session established with peer $peerUhid")
        val sender = localUhid
            ?: throw IllegalStateException(
                "Local UHID is not set. Call generatePreKeyBundle(uhid) " +
                    "or setLocalUhid(uhid) before encrypting."
            )

        // Lazy CKs initialization for the initiator's first send: X3DH placed
        // DHs and DHr but did not derive CKs (Signal §5 defers it until first
        // send to avoid an extra KDF if no message is ever sent).
        if (session.sendChainKey == null) {
            val remotePub = session.remoteEphemeralPub
                ?: throw IllegalStateException(
                    "Cannot derive sending chain: peer's ratchet public key is unknown."
                )
            dhRatchetSendOnly(session, remotePub)
        }

        var messageKey: ByteArray? = null
        try {
            val (newChain, mk) = ratchetChainKey(session.sendChainKey!!)
            session.sendChainKey = newChain
            messageKey = mk

            val nonce = ByteArray(AES_GCM_NONCE_SIZE).also { rng.nextBytes(it) }
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.ENCRYPT_MODE,
                SecretKeySpec(messageKey, "AES"),
                GCMParameterSpec(AES_GCM_TAG_SIZE * 8, nonce)
            )
            val ciphertext = cipher.doFinal(plaintext)

            val counter = session.sendCounter++
            val ratchetPub = session.myEphemeralPub.copyOf()

            return if (session.pendingPreKeyMessage) {
                // PreKey message: carries X3DH inputs so responder can mirror.
                // initiatorEphemeralKeyX25519 = senderEphemeralKeyX25519 because
                // the initiator's X3DH ephemeral becomes its first DH-ratchet pub.
                val payload = EncryptedPayload(
                    ciphertext = ciphertext,
                    nonce = nonce,
                    messageType = MESSAGE_TYPE_PRE_KEY,
                    senderUhid = sender,
                    counter = counter,
                    initiatorIdentityKeyX25519 = session.initiatorIdentityKeyX25519.copyOf(),
                    initiatorEphemeralKeyX25519 = ratchetPub.copyOf(),
                    usedSignedPreKeyId = session.usedSignedPreKeyId,
                    usedOneTimePreKeyId = session.usedOneTimePreKeyId,
                    senderEphemeralKeyX25519 = ratchetPub,
                    previousChainCount = session.previousChainCount,
                )
                session.pendingPreKeyMessage = false
                payload
            } else {
                EncryptedPayload(
                    ciphertext = ciphertext,
                    nonce = nonce,
                    messageType = MESSAGE_TYPE_NORMAL,
                    senderUhid = sender,
                    counter = counter,
                    senderEphemeralKeyX25519 = ratchetPub,
                    previousChainCount = session.previousChainCount,
                )
            }
        } finally {
            messageKey?.fill(0)
        }
    }

    fun decrypt(peerUhid: String, payload: EncryptedPayload): ByteArray {
        // Every Double-Ratchet message carries the sender's current ratchet
        // public key. Fall back to initiatorEphemeralKeyX25519 for backward
        // compat with older peers still on the pre-DR wire envelope.
        val senderRatchetPub = payload.senderEphemeralKeyX25519
            ?: payload.initiatorEphemeralKeyX25519

        if (payload.messageType == MESSAGE_TYPE_PRE_KEY) {
            val ik = payload.initiatorIdentityKeyX25519
                ?: throw IllegalArgumentException("PreKey message missing initiator identity key.")
            if (senderRatchetPub == null) {
                throw IllegalArgumentException(
                    "PreKey message missing initiator key material " +
                        "(senderEphemeralKeyX25519 / initiatorEphemeralKeyX25519)."
                )
            }
            establishResponderSession(
                peerUhid,
                ik,
                senderRatchetPub,
                payload.usedSignedPreKeyId,
                payload.usedOneTimePreKeyId
            )
        }

        val session = sessions[peerUhid]
            ?: throw IllegalStateException("No session established with peer $peerUhid")

        if (senderRatchetPub == null) {
            throw IllegalArgumentException(
                "Message missing senderEphemeralKeyX25519 — required for the Double Ratchet."
            )
        }

        // DH-ratchet step? Triggered when the peer's ratchet public key changes
        // (or hasn't been set yet — fresh responder session).
        val currentRemote = session.remoteEphemeralPub
        if (currentRemote == null || !constantTimeEquals(senderRatchetPub, currentRemote)) {
            // First, derive any skipped keys from the previous receive chain
            // (the chain keyed by the OLD remoteEphemeralPub). Then ratchet.
            skipMessageKeys(session, payload.previousChainCount)
            dhRatchetReceive(session, senderRatchetPub)
        }

        if (payload.ciphertext.size < AES_GCM_TAG_SIZE) {
            throw IllegalArgumentException("Ciphertext too short.")
        }

        var messageKey: ByteArray? = null
        try {
            // Skipped key cached for this (DHr_pub, counter) pair?
            val skKey = skippedKey(senderRatchetPub, payload.counter)
            val cached = session.skippedMessageKeys.remove(skKey)
            if (cached != null) {
                messageKey = cached
            } else {
                val recvChain = session.recvChainKey
                    ?: throw IllegalStateException(
                        "Receive chain not initialized (DH-ratchet step missing)."
                    )

                val gap = payload.counter - session.recvCounter
                if (gap > MAX_SKIPPED_KEYS) {
                    throw IllegalArgumentException(
                        "Message counter gap ($gap) exceeds maximum ($MAX_SKIPPED_KEYS). " +
                            "Session must be re-established."
                    )
                }

                // Skip ahead, caching intermediate keys keyed by (DHr, counter).
                var chain = recvChain
                while (session.recvCounter < payload.counter) {
                    val (nc, sk) = ratchetChainKey(chain)
                    chain = nc
                    session.skippedMessageKeys[skippedKey(senderRatchetPub, session.recvCounter)] = sk
                    session.recvCounter++
                }

                val (finalChain, mk) = ratchetChainKey(chain)
                session.recvChainKey = finalChain
                messageKey = mk
                session.recvCounter++
            }

            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.DECRYPT_MODE,
                SecretKeySpec(messageKey, "AES"),
                GCMParameterSpec(AES_GCM_TAG_SIZE * 8, payload.nonce)
            )
            return cipher.doFinal(payload.ciphertext)
        } finally {
            messageKey?.fill(0)
        }
    }

    /**
     * Generates (or refreshes) the local node's pre-key bundle.
     *
     * On every call, the OPK pool is topped up to [opkPoolSize] available
     * (un-issued) keys, then the next un-issued OPK id is dequeued and
     * returned in the bundle. The signed pre-key is generated lazily on the
     * first call and reused thereafter (rotation across calls is a future
     * extension; the C# reference rotates on a configurable interval).
     */
    fun generatePreKeyBundle(localUhid: String): PreKeyBundle {
        require(localUhid.isNotEmpty()) { "localUhid cannot be empty" }
        this.localUhid = localUhid

        val preKeyId: Int
        val otpkPub: ByteArray
        val spkPub: ByteArray
        val signedPreKeyId: Int
        val signature: ByteArray

        synchronized(preKeyLock) {
            // Signed pre-key: generated lazily on first call, reused after.
            if (preKeys.signedPreKeyPriv.isEmpty()) {
                val (spkPriv, fresh) = generateX25519KeyPair()
                val sig = Ed25519Service.sign(ed25519PrivateKey, fresh)
                preKeys.signedPreKeyId = randomPositiveInt()
                preKeys.signedPreKeyPriv = spkPriv
                preKeys.signedPreKeyPub = fresh
                preKeys.signedPreKeySignature = sig
            }

            // Top up the pool, then hand out the next un-issued id (FIFO).
            topUpOpkPoolNoLock()
            preKeyId = preKeys.availableOpkIds.removeFirst()
            otpkPub = preKeys.oneTimePreKeys[preKeyId]!!.second

            spkPub = preKeys.signedPreKeyPub
            signedPreKeyId = preKeys.signedPreKeyId
            signature = preKeys.signedPreKeySignature
        }

        return PreKeyBundle(
            uhid = localUhid,
            identityKey = ed25519PublicKey.copyOf(),
            identityKeyX25519 = identityX25519Pub.copyOf(),
            preKeyId = preKeyId,
            preKey = otpkPub.copyOf(),
            signedPreKeyId = signedPreKeyId,
            signedPreKey = spkPub.copyOf(),
            signedPreKeySignature = signature,
        )
    }

    /**
     * Tops the OPK pool up to [opkPoolSize] available (un-issued) keys.
     * Caller MUST hold [preKeyLock].
     *
     * Generates a fresh X25519 keypair per missing slot, assigns it a random
     * non-colliding id, and enqueues the id in [PreKeyStateInternal.availableOpkIds].
     * Idempotent — safe to call repeatedly.
     */
    private fun topUpOpkPoolNoLock() {
        while (preKeys.availableOpkIds.size < opkPoolSize) {
            val (priv, pub) = generateX25519KeyPair()

            // Choose a non-colliding id. The 2^31 range makes collisions in a
            // 100-element pool statistically negligible, but we still guard
            // explicitly. Mirrors the C# reference.
            var id: Int
            var attempts = 0
            do {
                id = randomPositiveInt()
                attempts++
                if (attempts > 64) {
                    throw IllegalStateException(
                        "Could not allocate a non-colliding OPK id after 64 attempts. " +
                            "Pool exhaustion or RNG failure.",
                    )
                }
            } while (preKeys.oneTimePreKeys.containsKey(id))

            preKeys.oneTimePreKeys[id] = priv to pub
            preKeys.availableOpkIds.addLast(id)
        }
    }

    /**
     * Number of OPKs currently held — both un-issued (in the available
     * queue) and issued-but-not-yet-consumed. Exposed for tests and
     * observability.
     */
    val heldOneTimePreKeyCount: Int
        get() = synchronized(preKeyLock) { preKeys.oneTimePreKeys.size }

    /**
     * Number of OPKs in the pool that have not yet been issued in any
     * bundle. Drops as bundles are issued; tops back up on the next
     * `generatePreKeyBundle()` call.
     */
    val availableOneTimePreKeyCount: Int
        get() = synchronized(preKeyLock) { preKeys.availableOpkIds.size }

    /**
     * Snapshot of the OPK pool: `(held, available)` — `held` is the total
     * (issued + un-issued); `available` is the un-issued portion. Mirrors
     * the C# `(HeldOneTimePreKeyCount, AvailableOneTimePreKeyCount)` pair.
     */
    fun getOpkPoolStatus(): Pair<Int, Int> = synchronized(preKeyLock) {
        preKeys.oneTimePreKeys.size to preKeys.availableOpkIds.size
    }

    /**
     * Establishes an initiator-side session against a pre-key bundle: runs
     * the four X3DH DHs (Signal §3.3) over X25519, derives the root key, and
     * primes the Double Ratchet by adopting the X3DH ephemeral as the
     * initiator's first DHs. The peer's signed pre-key becomes the initial
     * DHr. The first encrypt() after this returns a PreKey message.
     */
    fun processPreKeyBundle(bundle: PreKeyBundle) {
        if (!Ed25519Service.verify(bundle.identityKey, bundle.signedPreKey, bundle.signedPreKeySignature)) {
            throw IllegalArgumentException("Signed pre-key signature verification failed.")
        }
        require(bundle.identityKeyX25519.size == X25519_PUBLIC_KEY_SIZE) {
            "Bundle has malformed X25519 identity key (length ${bundle.identityKeyX25519.size})"
        }
        require(bundle.signedPreKey.size == X25519_PUBLIC_KEY_SIZE) {
            "Bundle has malformed signed pre-key (length ${bundle.signedPreKey.size})"
        }
        require(bundle.preKey.size == X25519_PUBLIC_KEY_SIZE) {
            "Bundle has malformed one-time pre-key (length ${bundle.preKey.size})"
        }

        // Fresh ephemeral X25519 keypair, generated per-session. This becomes
        // the initiator's first DH-ratchet keypair (Signal-canonical X3DH↔DR
        // integration).
        val (ekPriv, ekPub) = generateX25519KeyPair()

        var dh1: ByteArray? = null
        var dh2: ByteArray? = null
        var dh3: ByteArray? = null
        var dh4: ByteArray? = null
        var shared: ByteArray? = null

        try {
            // X3DH 4-DH key agreement (initiator side).
            dh1 = x25519Agree(identityX25519Priv, bundle.signedPreKey)
            dh2 = x25519Agree(ekPriv, bundle.identityKeyX25519)
            dh3 = x25519Agree(ekPriv, bundle.signedPreKey)
            dh4 = x25519Agree(ekPriv, bundle.preKey)

            shared = dh1 + dh2 + dh3 + dh4
            val rootKey = hkdf32(shared, HKDF_ROOT_INFO)

            // Adopt the X3DH ephemeral as initial DHs; peer's SPK is initial DHr.
            // CKs is computed lazily on first send (dhRatchetSendOnly).
            val session = SignalSession().apply {
                this.rootKey = rootKey
                this.sendChainKey = null
                this.recvChainKey = null
                this.myEphemeralPriv = ekPriv
                this.myEphemeralPub = ekPub.copyOf()
                this.remoteEphemeralPub = bundle.signedPreKey.copyOf()
                this.pendingPreKeyMessage = true
                this.initiatorIdentityKeyX25519 = identityX25519Pub.copyOf()
                this.usedSignedPreKeyId = bundle.signedPreKeyId
                this.usedOneTimePreKeyId = bundle.preKeyId
            }

            sessions[bundle.uhid] = session
        } finally {
            dh1?.fill(0)
            dh2?.fill(0)
            dh3?.fill(0)
            dh4?.fill(0)
            shared?.fill(0)
            // Note: ekPriv ownership transferred to session (do not zero here).
        }
    }

    /**
     * Mirrors the initiator's 4 X3DH DHs to derive the same root key. Adopts
     * the signed pre-key (private + public) as the responder's initial DHs
     * (Signal-canonical responder bootstrap). Leaves remoteEphemeralPub null,
     * so the very next decrypt() triggers a DH-ratchet step that rotates the
     * SPK to a fresh DHs and derives both chain keys. Consumes (and zeros)
     * the one-time pre-key.
     */
    private fun establishResponderSession(
        peerUhid: String,
        initiatorIK: ByteArray,
        initiatorRatchetPub: ByteArray,
        usedSignedPreKeyId: Int,
        usedOneTimePreKeyId: Int,
    ) {
        require(initiatorIK.size == X25519_PUBLIC_KEY_SIZE) {
            "Initiator IK_X25519 wrong size: ${initiatorIK.size}"
        }
        require(initiatorRatchetPub.size == X25519_PUBLIC_KEY_SIZE) {
            "Initiator ratchet pub wrong size: ${initiatorRatchetPub.size}"
        }
        // Atomically resolve SPK + OPK private halves. Holding [preKeyLock]
        // for the lookup ensures concurrent responder-side establishments
        // race-free against the OPK pool: the *first* responder to reach this
        // point for a given OPK id removes it from the map; later attempts
        // surface as "OPK not held" which is the correct replay-rejection
        // signal.
        val spkPrivCopy: ByteArray
        val spkPubCopy: ByteArray
        val otpkPriv: ByteArray
        synchronized(preKeyLock) {
            check(preKeys.signedPreKeyId == usedSignedPreKeyId && preKeys.signedPreKeyPriv.isNotEmpty()) {
                "PreKey message references signed pre-key id $usedSignedPreKeyId which is not held by this node."
            }
            val otpk = preKeys.oneTimePreKeys.remove(usedOneTimePreKeyId)
                ?: throw IllegalStateException(
                    "PreKey message references one-time pre-key id $usedOneTimePreKeyId which is not held (already consumed?).",
                )
            spkPrivCopy = preKeys.signedPreKeyPriv.copyOf()
            spkPubCopy = preKeys.signedPreKeyPub.copyOf()
            otpkPriv = otpk.first
        }

        var dh1: ByteArray? = null
        var dh2: ByteArray? = null
        var dh3: ByteArray? = null
        var dh4: ByteArray? = null
        var shared: ByteArray? = null

        try {
            // Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
            dh1 = x25519Agree(spkPrivCopy, initiatorIK)
            dh2 = x25519Agree(identityX25519Priv, initiatorRatchetPub)
            dh3 = x25519Agree(spkPrivCopy, initiatorRatchetPub)
            dh4 = x25519Agree(otpkPriv, initiatorRatchetPub)

            shared = dh1 + dh2 + dh3 + dh4
            val rootKey = hkdf32(shared, HKDF_ROOT_INFO)

            // Adopt SPK as the initial DHs. The DH-ratchet step that follows
            // (triggered by the very first decrypt below) will rotate it to
            // a fresh keypair and derive both chain keys.
            val session = SignalSession().apply {
                this.rootKey = rootKey
                this.sendChainKey = null
                this.recvChainKey = null
                this.myEphemeralPriv = spkPrivCopy
                this.myEphemeralPub = spkPubCopy
                this.remoteEphemeralPub = null   // forces DH-ratchet on first decrypt
                this.pendingPreKeyMessage = false
            }

            sessions[peerUhid] = session

            // Zero the consumed one-time pre-key — replay protection at the
            // bundle layer. Removal from the map already happened atomically
            // above.
            otpkPriv.fill(0)
        } finally {
            dh1?.fill(0)
            dh2?.fill(0)
            dh3?.fill(0)
            dh4?.fill(0)
            shared?.fill(0)
        }
    }

    // ─── Double-Ratchet primitives (Signal §5.2) ────────────────────────────

    /**
     * Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
     * derives a new receiving chain via KDF_RK(RK, DH(DHs, newDHr)), generates
     * a fresh DHs, and derives a new sending chain via
     * KDF_RK(RK, DH(newDHs, newDHr)).
     */
    private fun dhRatchetReceive(session: SignalSession, newRemoteEphemeralPub: ByteArray) {
        // Save send-counter as PN so the peer can compute skipped keys
        // across the ratchet boundary on subsequent decrypts.
        session.previousChainCount = session.sendCounter
        session.sendCounter = 0
        session.recvCounter = 0
        session.remoteEphemeralPub = newRemoteEphemeralPub.copyOf()

        // Step 1: derive new receiving chain from current DHs · new DHr.
        var dh1: ByteArray? = null
        try {
            dh1 = x25519Agree(session.myEphemeralPriv, session.remoteEphemeralPub!!)
            val (newRoot, newCkr) = kdfRk(session.rootKey, dh1)
            // Zero the old root before overwriting.
            session.rootKey.fill(0)
            session.rootKey = newRoot
            session.recvChainKey?.fill(0)
            session.recvChainKey = newCkr
        } finally {
            dh1?.fill(0)
        }

        // Step 2: rotate DHs to a fresh keypair, derive new sending chain
        // from new DHs · new DHr.
        session.myEphemeralPriv.fill(0)
        val (newPriv, newPub) = generateX25519KeyPair()
        session.myEphemeralPriv = newPriv
        session.myEphemeralPub = newPub

        var dh2: ByteArray? = null
        try {
            dh2 = x25519Agree(session.myEphemeralPriv, session.remoteEphemeralPub!!)
            val (newRoot2, newCks) = kdfRk(session.rootKey, dh2)
            session.rootKey.fill(0)
            session.rootKey = newRoot2
            session.sendChainKey?.fill(0)
            session.sendChainKey = newCks
        } finally {
            dh2?.fill(0)
        }
    }

    /**
     * Lazy half-ratchet for the very first send on a freshly-established
     * initiator session. DHs and DHr are already set (X3DH placed them); we
     * just derive the sending chain. We do NOT rotate DHs here — only on a
     * true DH-ratchet (i.e. on receive).
     */
    private fun dhRatchetSendOnly(session: SignalSession, remotePub: ByteArray) {
        var dh: ByteArray? = null
        try {
            dh = x25519Agree(session.myEphemeralPriv, remotePub)
            val (newRoot, newCks) = kdfRk(session.rootKey, dh)
            session.rootKey.fill(0)
            session.rootKey = newRoot
            session.sendChainKey?.fill(0)
            session.sendChainKey = newCks
        } finally {
            dh?.fill(0)
        }
    }

    /**
     * Saves any unread message keys on the current receive chain up to the
     * given counter, so they can be consumed if those messages eventually
     * arrive after a DH-ratchet step. Bounded by [MAX_SKIPPED_KEYS].
     */
    private fun skipMessageKeys(session: SignalSession, until: Int) {
        val recvChain = session.recvChainKey ?: return
        val remotePub = session.remoteEphemeralPub ?: return
        if (until <= session.recvCounter) return
        if (until - session.recvCounter > MAX_SKIPPED_KEYS) {
            throw IllegalArgumentException(
                "Skipped-key request exceeds maximum ($MAX_SKIPPED_KEYS). Session must be re-established."
            )
        }

        var chain = recvChain
        while (session.recvCounter < until) {
            val (nc, sk) = ratchetChainKey(chain)
            chain = nc
            session.skippedMessageKeys[skippedKey(remotePub, session.recvCounter)] = sk
            session.recvCounter++
        }
        session.recvChainKey = chain
    }

    fun signData(data: ByteArray): ByteArray = Ed25519Service.sign(ed25519PrivateKey, data)

    fun verifySignature(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean =
        Ed25519Service.verify(publicKey, data, signature)

    fun getPublicKey(): ByteArray = ed25519PublicKey.copyOf()

    fun getX25519PublicKey(): ByteArray = identityX25519Pub.copyOf()

    // ─── Crypto primitives ──────────────────────────────────────────────────

    /** Fresh X25519 keypair — raw 32-byte private + 32-byte public, RFC 7748. */
    private fun generateX25519KeyPair(): Pair<ByteArray, ByteArray> {
        val gen = X25519KeyPairGenerator()
        gen.init(X25519KeyGenerationParameters(rng))
        val kp = gen.generateKeyPair()
        val priv = kp.private as X25519PrivateKeyParameters
        val pub = kp.public as X25519PublicKeyParameters
        return priv.encoded to pub.encoded
    }

    /**
     * X25519 ECDH. Returns 32 raw shared-secret bytes.
     *
     * RFC 7748 §6.1: detect the all-zero output (small-subgroup attack).
     */
    private fun x25519Agree(localPriv: ByteArray, remotePub: ByteArray): ByteArray {
        val priv = X25519PrivateKeyParameters(localPriv, 0)
        val pub = X25519PublicKeyParameters(remotePub, 0)
        val agreement = X25519Agreement()
        agreement.init(priv)
        val shared = ByteArray(agreement.agreementSize)
        agreement.calculateAgreement(pub, shared, 0)
        var nonZero = 0
        for (b in shared) nonZero = nonZero or b.toInt()
        if ((nonZero and 0xFF) == 0) {
            shared.fill(0)
            throw IllegalStateException("X25519 produced an all-zero shared secret (low-order point)")
        }
        return shared
    }

    /** HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# HKDF.DeriveKey. */
    private fun hkdf32(ikm: ByteArray, info: ByteArray): ByteArray {
        // Extract: PRK = HMAC-SHA256(salt=0x00*32, IKM)
        val salt = ByteArray(32) // RFC 5869: salt absent => salt = HashLen zeros.
        val hmacExtract = Mac.getInstance("HmacSHA256")
        hmacExtract.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = hmacExtract.doFinal(ikm)

        // Expand: T(1) = HMAC(PRK, info || 0x01); we only need 32 bytes.
        val hmacExpand = Mac.getInstance("HmacSHA256")
        hmacExpand.init(SecretKeySpec(prk, "HmacSHA256"))
        hmacExpand.update(info)
        hmacExpand.update(0x01.toByte())
        val t = hmacExpand.doFinal()
        return t.copyOf(32)
    }

    /**
     * KDF_RK per Signal §5.2: derives a new root key + new chain key from
     * the current root key and a fresh DH output. HKDF-SHA256 over 64 bytes;
     * first 32 = new root, second 32 = new chain key.
     *
     * Matches C# HKDF.DeriveKey(SHA256, ikm=dhOutput, len=64,
     * salt=rootKey, info=HkdfRatchetInfo). Implemented manually because
     * 64-byte output requires two HMAC blocks (T1 and T2).
     */
    private fun kdfRk(rootKey: ByteArray, dhOutput: ByteArray): Pair<ByteArray, ByteArray> {
        // RFC 5869 Extract: PRK = HMAC(salt=rootKey, ikm=dhOutput).
        val extract = Mac.getInstance("HmacSHA256")
        extract.init(SecretKeySpec(rootKey, "HmacSHA256"))
        val prk = extract.doFinal(dhOutput)

        // Expand for 64 bytes => two blocks.
        // T(1) = HMAC(PRK, "" || info || 0x01)
        val expand1 = Mac.getInstance("HmacSHA256")
        expand1.init(SecretKeySpec(prk, "HmacSHA256"))
        expand1.update(HKDF_RATCHET_INFO)
        expand1.update(0x01.toByte())
        val t1 = expand1.doFinal()

        // T(2) = HMAC(PRK, T(1) || info || 0x02)
        val expand2 = Mac.getInstance("HmacSHA256")
        expand2.init(SecretKeySpec(prk, "HmacSHA256"))
        expand2.update(t1)
        expand2.update(HKDF_RATCHET_INFO)
        expand2.update(0x02.toByte())
        val t2 = expand2.doFinal()

        // First 32 bytes (T1) = new root key; next 32 bytes (T2[0..32]) = new chain key.
        val newRoot = t1.copyOf(32)
        val newChain = t2.copyOf(32)

        // Zero PRK, T1, T2.
        prk.fill(0)
        t1.fill(0)
        t2.fill(0)

        return newRoot to newChain
    }

    /**
     * Advances a chain key by one step per Signal §5.1.
     *
     *   message_key   = HMAC-SHA256(chain_key, 0x01)
     *   new_chain_key = HMAC-SHA256(chain_key, 0x02)
     */
    private fun ratchetChainKey(chainKey: ByteArray): Pair<ByteArray, ByteArray> {
        val mac1 = Mac.getInstance("HmacSHA256")
        mac1.init(SecretKeySpec(chainKey, "HmacSHA256"))
        val messageKey = mac1.doFinal(RATCHET_MESSAGE_KEY_INPUT)

        val mac2 = Mac.getInstance("HmacSHA256")
        mac2.init(SecretKeySpec(chainKey, "HmacSHA256"))
        val newChainKey = mac2.doFinal(RATCHET_CHAIN_KEY_INPUT)

        return newChainKey to messageKey
    }

    /** Composite key for the skipped-message-keys map: "Hex(DHr_pub):counter". */
    private fun skippedKey(dhrPub: ByteArray, counter: Int): String {
        val sb = StringBuilder(dhrPub.size * 2 + 12)
        for (b in dhrPub) {
            val v = b.toInt() and 0xFF
            sb.append(HEX_CHARS[v ushr 4])
            sb.append(HEX_CHARS[v and 0x0F])
        }
        sb.append(':').append(counter)
        return sb.toString()
    }

    private fun constantTimeEquals(a: ByteArray, b: ByteArray): Boolean {
        if (a.size != b.size) return false
        return MessageDigest.isEqual(a, b)
    }

    private fun randomPositiveInt(): Int {
        var n = rng.nextInt() and 0x7FFFFFFF
        if (n == 0) n = 1
        return n
    }
}

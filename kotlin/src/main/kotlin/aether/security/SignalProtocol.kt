// SPDX-License-Identifier: MIT

package aether.security

import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.generators.X25519KeyPairGenerator
import org.bouncycastle.crypto.params.X25519KeyGenerationParameters
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Signal Protocol implementation: X3DH + Double-Ratchet.
 *
 * Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
 *   DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
 *   DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
 *   DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
 *   DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
 *
 * Root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
 * Symmetric ratchet: HMAC-SHA256, single-byte domain separation
 *   (0x01 -> message key, 0x02 -> next chain key) per Signal §5.1.
 * Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
 * Identity signing: Ed25519.
 */

/** Wire-level encrypted payload. */
data class EncryptedPayload(
    val ciphertext: ByteArray,
    val nonce: ByteArray,
    /** 0 = normal, 1 = PreKey (initial). */
    val messageType: Int,
    val senderUhid: String,
    val counter: Int,
    /** PreKey messages: initiator's long-term X25519 identity public key (32 bytes). */
    val initiatorIdentityKeyX25519: ByteArray? = null,
    /** PreKey messages: initiator's ephemeral X25519 public key (32 bytes). */
    val initiatorEphemeralKeyX25519: ByteArray? = null,
    /** PreKey messages: SignedPreKeyId from the recipient bundle the initiator consumed. */
    val usedSignedPreKeyId: Int = 0,
    /** PreKey messages: one-time PreKeyId from the recipient bundle the initiator consumed. */
    val usedOneTimePreKeyId: Int = 0,
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
 * Signal session state.
 *
 * On the initiator side, [pendingPreKeyMessage] is true until the first
 * outbound message is sent. While true, the next encrypt() emits a PreKey
 * message carrying the four `initiator*` fields below.
 */
internal class SignalSession(
    var rootKey: ByteArray,
    var sendChainKey: ByteArray,
    var recvChainKey: ByteArray,
    var sendCounter: Int = 0,
    var recvCounter: Int = 0,
    val skippedMessageKeys: MutableMap<Int, ByteArray> = mutableMapOf(),
    var pendingPreKeyMessage: Boolean = false,
    var initiatorIdentityKeyX25519: ByteArray = ByteArray(0),
    var initiatorEphemeralKeyX25519: ByteArray = ByteArray(0),
    var usedSignedPreKeyId: Int = 0,
    var usedOneTimePreKeyId: Int = 0,
)

/** Responder-side pre-key state. */
internal class PreKeyStateInternal {
    var signedPreKeyId: Int = 0
    var signedPreKeyPriv: ByteArray = ByteArray(0)
    var signedPreKeyPub: ByteArray = ByteArray(0)
    var signedPreKeySignature: ByteArray = ByteArray(0)
    val oneTimePreKeys: MutableMap<Int, Pair<ByteArray, ByteArray>> = mutableMapOf()
}

class SignalProtocol {
    companion object {
        const val MAX_SKIPPED_KEYS: Int = 1000

        const val MESSAGE_TYPE_NORMAL: Int = 0
        const val MESSAGE_TYPE_PRE_KEY: Int = 1

        private const val AES_KEY_SIZE = 32
        private const val AES_GCM_NONCE_SIZE = 12
        private const val AES_GCM_TAG_SIZE = 16
        private const val X25519_PUBLIC_KEY_SIZE = 32

        // HKDF info strings — these MUST match the C# reference exactly. Any
        // drift breaks cross-language interop (verified by
        // fixtures/signal/expected/x3dh_basic.json).
        private val HKDF_ROOT_INFO = "aether-x3dh-root-v1".toByteArray(Charsets.UTF_8)
        private val HKDF_CHAIN_INITIATOR_SEND_INFO = "aether-chain-initiator-send-v1".toByteArray(Charsets.UTF_8)
        private val HKDF_CHAIN_INITIATOR_RECV_INFO = "aether-chain-initiator-recv-v1".toByteArray(Charsets.UTF_8)

        private val rng = SecureRandom()
    }

    private val sessions = ConcurrentHashMap<String, SignalSession>()

    // Long-term identity keys — two distinct keypairs per node.
    private val identityX25519Priv: ByteArray
    private val identityX25519Pub: ByteArray
    private val ed25519PrivateKey: ByteArray
    private val ed25519PublicKey: ByteArray

    private var localUhid: String? = null
    private val preKeys: PreKeyStateInternal = PreKeyStateInternal()

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

        val (newChain, messageKey) = ratchetChainKey(session.sendChainKey)
        session.sendChainKey = newChain

        try {
            val nonce = ByteArray(AES_GCM_NONCE_SIZE).also { rng.nextBytes(it) }
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.ENCRYPT_MODE,
                SecretKeySpec(messageKey, "AES"),
                GCMParameterSpec(AES_GCM_TAG_SIZE * 8, nonce)
            )
            val ciphertext = cipher.doFinal(plaintext)

            val counter = session.sendCounter++

            return if (session.pendingPreKeyMessage) {
                val payload = EncryptedPayload(
                    ciphertext = ciphertext,
                    nonce = nonce,
                    messageType = MESSAGE_TYPE_PRE_KEY,
                    senderUhid = sender,
                    counter = counter,
                    initiatorIdentityKeyX25519 = session.initiatorIdentityKeyX25519.copyOf(),
                    initiatorEphemeralKeyX25519 = session.initiatorEphemeralKeyX25519.copyOf(),
                    usedSignedPreKeyId = session.usedSignedPreKeyId,
                    usedOneTimePreKeyId = session.usedOneTimePreKeyId,
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
                )
            }
        } finally {
            messageKey.fill(0)
        }
    }

    fun decrypt(peerUhid: String, payload: EncryptedPayload): ByteArray {
        if (payload.messageType == MESSAGE_TYPE_PRE_KEY) {
            val ik = payload.initiatorIdentityKeyX25519
                ?: throw IllegalArgumentException("PreKey message missing initiator identity key.")
            val ek = payload.initiatorEphemeralKeyX25519
                ?: throw IllegalArgumentException("PreKey message missing initiator ephemeral key.")
            establishResponderSession(peerUhid, ik, ek, payload.usedSignedPreKeyId, payload.usedOneTimePreKeyId)
        }

        val session = sessions[peerUhid]
            ?: throw IllegalStateException("No session established with peer $peerUhid")

        if (payload.ciphertext.size < AES_GCM_TAG_SIZE) {
            throw IllegalArgumentException("Ciphertext too short.")
        }

        var messageKey: ByteArray? = null
        try {
            val cached = session.skippedMessageKeys.remove(payload.counter)
            if (cached != null) {
                messageKey = cached
            } else {
                val gap = payload.counter - session.recvCounter
                if (gap > MAX_SKIPPED_KEYS) {
                    throw IllegalArgumentException(
                        "Message counter gap ($gap) exceeds maximum ($MAX_SKIPPED_KEYS). Session must be re-established."
                    )
                }
                while (session.recvCounter < payload.counter) {
                    val (nc, sk) = ratchetChainKey(session.recvChainKey)
                    session.recvChainKey = nc
                    session.skippedMessageKeys[session.recvCounter] = sk
                    session.recvCounter++
                }
                val (nc, mk) = ratchetChainKey(session.recvChainKey)
                session.recvChainKey = nc
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

    fun generatePreKeyBundle(localUhid: String): PreKeyBundle {
        require(localUhid.isNotEmpty()) { "localUhid cannot be empty" }
        this.localUhid = localUhid

        // One-time pre-key.
        val (otpkPriv, otpkPub) = generateX25519KeyPair()
        val preKeyId = randomPositiveInt()
        preKeys.oneTimePreKeys[preKeyId] = otpkPriv to otpkPub

        // Signed pre-key.
        val (spkPriv, spkPub) = generateX25519KeyPair()
        val signedPreKeyId = randomPositiveInt()
        val signature = Ed25519Service.sign(ed25519PrivateKey, spkPub)
        preKeys.signedPreKeyId = signedPreKeyId
        preKeys.signedPreKeyPriv = spkPriv
        preKeys.signedPreKeyPub = spkPub
        preKeys.signedPreKeySignature = signature

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

        // Fresh ephemeral X25519 keypair, generated per-session.
        val (ekPriv, ekPub) = generateX25519KeyPair()

        try {
            // X3DH 4-DH key agreement (initiator side).
            val dh1 = x25519Agree(identityX25519Priv, bundle.signedPreKey)
            val dh2 = x25519Agree(ekPriv, bundle.identityKeyX25519)
            val dh3 = x25519Agree(ekPriv, bundle.signedPreKey)
            val dh4 = x25519Agree(ekPriv, bundle.preKey)

            val shared = dh1 + dh2 + dh3 + dh4
            val rootKey = hkdf32(shared, HKDF_ROOT_INFO)
            val sendChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_SEND_INFO)
            val recvChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_RECV_INFO)

            sessions[bundle.uhid] = SignalSession(
                rootKey = rootKey,
                sendChainKey = sendChain,
                recvChainKey = recvChain,
                pendingPreKeyMessage = true,
                initiatorIdentityKeyX25519 = identityX25519Pub.copyOf(),
                initiatorEphemeralKeyX25519 = ekPub.copyOf(),
                usedSignedPreKeyId = bundle.signedPreKeyId,
                usedOneTimePreKeyId = bundle.preKeyId,
            )

            shared.fill(0); dh1.fill(0); dh2.fill(0); dh3.fill(0); dh4.fill(0)
        } finally {
            ekPriv.fill(0)
        }
    }

    /**
     * Mirrors the initiator's 4 X3DH DHs to derive the same root key, then
     * derives chain keys with send/recv roles SWAPPED relative to the
     * initiator. Consumes (and zeros) the one-time pre-key.
     */
    private fun establishResponderSession(
        peerUhid: String,
        initiatorIK: ByteArray,
        initiatorEK: ByteArray,
        usedSignedPreKeyId: Int,
        usedOneTimePreKeyId: Int,
    ) {
        require(initiatorIK.size == X25519_PUBLIC_KEY_SIZE) {
            "Initiator IK_X25519 wrong size: ${initiatorIK.size}"
        }
        require(initiatorEK.size == X25519_PUBLIC_KEY_SIZE) {
            "Initiator EK_X25519 wrong size: ${initiatorEK.size}"
        }
        check(preKeys.signedPreKeyId == usedSignedPreKeyId && preKeys.signedPreKeyPriv.isNotEmpty()) {
            "PreKey message references signed pre-key id $usedSignedPreKeyId which is not held by this node."
        }
        val otpk = preKeys.oneTimePreKeys[usedOneTimePreKeyId]
            ?: throw IllegalStateException(
                "PreKey message references one-time pre-key id $usedOneTimePreKeyId which is not held (already consumed?)."
            )

        // Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
        val dh1 = x25519Agree(preKeys.signedPreKeyPriv, initiatorIK)
        val dh2 = x25519Agree(identityX25519Priv, initiatorEK)
        val dh3 = x25519Agree(preKeys.signedPreKeyPriv, initiatorEK)
        val dh4 = x25519Agree(otpk.first, initiatorEK)

        val shared = dh1 + dh2 + dh3 + dh4
        val rootKey = hkdf32(shared, HKDF_ROOT_INFO)
        // SWAPPED: initiator's send-chain info derives our recv-chain.
        val recvChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_SEND_INFO)
        val sendChain = hkdf32(rootKey, HKDF_CHAIN_INITIATOR_RECV_INFO)

        sessions[peerUhid] = SignalSession(
            rootKey = rootKey,
            sendChainKey = sendChain,
            recvChainKey = recvChain,
        )

        // Consume one-time pre-key — never reuse.
        otpk.first.fill(0)
        preKeys.oneTimePreKeys.remove(usedOneTimePreKeyId)

        shared.fill(0); dh1.fill(0); dh2.fill(0); dh3.fill(0); dh4.fill(0)
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

        // Expand: T(1) = HMAC(PRK, info || 0x01); we only need 32 bytes, so one block.
        val hmacExpand = Mac.getInstance("HmacSHA256")
        hmacExpand.init(SecretKeySpec(prk, "HmacSHA256"))
        hmacExpand.update(info)
        hmacExpand.update(0x01.toByte())
        val t = hmacExpand.doFinal()
        return t.copyOf(32)
    }

    /** Single Double-Ratchet step (Signal §5.1). */
    private fun ratchetChainKey(chainKey: ByteArray): Pair<ByteArray, ByteArray> {
        val mac1 = Mac.getInstance("HmacSHA256")
        mac1.init(SecretKeySpec(chainKey, "HmacSHA256"))
        val messageKey = mac1.doFinal(byteArrayOf(0x01))

        val mac2 = Mac.getInstance("HmacSHA256")
        mac2.init(SecretKeySpec(chainKey, "HmacSHA256"))
        val newChainKey = mac2.doFinal(byteArrayOf(0x02))

        return newChainKey to messageKey
    }

    private fun randomPositiveInt(): Int {
        var n = rng.nextInt() and 0x7FFFFFFF
        if (n == 0) n = 1
        return n
    }
}

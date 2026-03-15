// SPDX-License-Identifier: MIT

package aether.security

import aether.AetherConstants
import org.bouncycastle.jcajce.provider.digest.SHA256
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.X509EncodedKeySpec
import java.util.concurrent.ConcurrentHashMap
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec
import kotlin.math.min

/**
 * Data class representing an encrypted payload.
 */
data class EncryptedPayload(
    val ciphertext: ByteArray,
    val nonce: ByteArray,
    val messageType: Int,
    val senderUhid: String,
    val counter: Int
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EncryptedPayload) return false

        if (!ciphertext.contentEquals(other.ciphertext)) return false
        if (!nonce.contentEquals(other.nonce)) return false
        if (messageType != other.messageType) return false
        if (senderUhid != other.senderUhid) return false
        if (counter != other.counter) return false

        return true
    }

    override fun hashCode(): Int {
        var result = ciphertext.contentHashCode()
        result = 31 * result + nonce.contentHashCode()
        result = 31 * result + messageType
        result = 31 * result + senderUhid.hashCode()
        result = 31 * result + counter
        return result
    }
}

/**
 * Data class representing a pre-key bundle for session establishment.
 */
data class PreKeyBundle(
    val uhid: String,
    val identityKey: ByteArray,
    val preKeyId: Int,
    val preKey: ByteArray,
    val signedPreKeyId: Int,
    val signedPreKey: ByteArray,
    val signedPreKeySignature: ByteArray
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PreKeyBundle) return false

        if (uhid != other.uhid) return false
        if (!identityKey.contentEquals(other.identityKey)) return false
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
        result = 31 * result + preKeyId
        result = 31 * result + preKey.contentHashCode()
        result = 31 * result + signedPreKeyId
        result = 31 * result + signedPreKey.contentHashCode()
        result = 31 * result + signedPreKeySignature.contentHashCode()
        return result
    }
}

/**
 * Internal session state tracking.
 */
internal data class SignalSession(
    var rootKey: ByteArray,
    var sendChainKey: ByteArray,
    var recvChainKey: ByteArray,
    var sendCounter: Int = 0,
    var recvCounter: Int = 0,
    var remotePublicKey: ByteArray = ByteArray(0),
    val skippedMessageKeys: MutableMap<Int, ByteArray> = mutableMapOf()
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SignalSession) return false

        if (!rootKey.contentEquals(other.rootKey)) return false
        if (!sendChainKey.contentEquals(other.sendChainKey)) return false
        if (!recvChainKey.contentEquals(other.recvChainKey)) return false
        if (sendCounter != other.sendCounter) return false
        if (recvCounter != other.recvCounter) return false
        if (!remotePublicKey.contentEquals(other.remotePublicKey)) return false
        if (skippedMessageKeys != other.skippedMessageKeys) return false

        return true
    }

    override fun hashCode(): Int {
        var result = rootKey.contentHashCode()
        result = 31 * result + sendChainKey.contentHashCode()
        result = 31 * result + recvChainKey.contentHashCode()
        result = 31 * result + sendCounter
        result = 31 * result + recvCounter
        result = 31 * result + remotePublicKey.contentHashCode()
        result = 31 * result + skippedMessageKeys.hashCode()
        return result
    }
}

/**
 * Signal Protocol implementation providing end-to-end encryption for Aether mesh messaging.
 *
 * Key agreement: X3DH with ECDH P-256
 * Key derivation: HKDF-SHA256 with unique info strings per derivation context
 * Encryption: AES-256-GCM with 12-byte nonce and 16-byte authentication tag
 * Signing: Ed25519
 *
 * The symmetric ratchet advances the chain key with each message sent or received.
 * Out-of-order messages are handled by caching skipped keys (up to MaxSkippedKeys).
 */
class SignalProtocol {
    companion object {
        const val MAX_SKIPPED_KEYS = AetherConstants.MAX_SKIPPED_KEYS
    }

    private val sessions = ConcurrentHashMap<String, SignalSession>()
    private var ed25519PrivateKey = ByteArray(0)
    private var ed25519PublicKey = ByteArray(0)

    init {
        // Generate Ed25519 identity keys
        val (privKey, pubKey) = Ed25519Service.generateKeyPair()
        ed25519PrivateKey = privKey
        ed25519PublicKey = pubKey
    }

    /**
     * Checks if a session exists with a peer.
     */
    fun hasSession(peerUhid: String): Boolean = sessions.containsKey(peerUhid)

    /**
     * Encrypts plaintext for a peer using the established Signal session.
     *
     * @param peerUhid Target peer UHID
     * @param plaintext Data to encrypt
     * @return EncryptedPayload with ciphertext and metadata
     * @throws IllegalStateException if no session exists with the peer
     */
    fun encrypt(peerUhid: String, plaintext: ByteArray): EncryptedPayload {
        val session = sessions[peerUhid]
            ?: throw IllegalStateException("No session established with peer $peerUhid")

        // Ratchet the sending chain
        val (newChainKey, messageKey) = ratchetChainKey(session.sendChainKey, AetherConstants.HKDF_CHAIN_SEND_INFO)
        session.sendChainKey = newChainKey

        try {
            // Encrypt with AES-256-GCM
            val nonce = ByteArray(AetherConstants.AES_GCM_NONCE_SIZE)
            SecureRandom().nextBytes(nonce)

            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(messageKey, 0, messageKey.size, "AES"), IvParameterSpec(nonce))

            val ciphertext = cipher.doFinal(plaintext)
            val counter = session.sendCounter++

            return EncryptedPayload(
                ciphertext = ciphertext,
                nonce = nonce,
                messageType = 0,
                senderUhid = peerUhid,
                counter = counter
            )
        } finally {
            messageKey.fill(0)
        }
    }

    /**
     * Decrypts a ciphertext using the established Signal session.
     *
     * Handles out-of-order messages by caching skipped keys.
     *
     * @param peerUhid Source peer UHID
     * @param payload EncryptedPayload to decrypt
     * @return Plaintext
     * @throws IllegalStateException if no session exists
     * @throws IllegalArgumentException if message gap is too large
     */
    fun decrypt(peerUhid: String, payload: EncryptedPayload): ByteArray {
        val session = sessions[peerUhid]
            ?: throw IllegalStateException("No session established with peer $peerUhid")

        var messageKey: ByteArray? = null
        try {
            // Check if this is a skipped message
            if (session.skippedMessageKeys.containsKey(payload.counter)) {
                messageKey = session.skippedMessageKeys.remove(payload.counter)
                    ?: throw IllegalStateException("Skipped key was removed before use")
            } else {
                // Check for excessive counter gap
                val gap = payload.counter - session.recvCounter
                if (gap > MAX_SKIPPED_KEYS) {
                    throw IllegalArgumentException(
                        "Message counter gap ($gap) exceeds maximum ($MAX_SKIPPED_KEYS). Session must be re-established."
                    )
                }

                // Skip ahead and cache intermediate keys
                while (session.recvCounter < payload.counter) {
                    val (newChainKey, skipKey) = ratchetChainKey(session.recvChainKey, AetherConstants.HKDF_CHAIN_RECV_INFO)
                    session.recvChainKey = newChainKey
                    session.skippedMessageKeys[session.recvCounter] = skipKey
                    session.recvCounter++
                }

                // Derive the actual message key
                val (newChainKey, key) = ratchetChainKey(session.recvChainKey, AetherConstants.HKDF_CHAIN_RECV_INFO)
                session.recvChainKey = newChainKey
                messageKey = key
                session.recvCounter++
            }

            // Decrypt with AES-GCM
            if (payload.ciphertext.size < AetherConstants.AES_GCM_TAG_SIZE) {
                throw IllegalArgumentException("Ciphertext too short.")
            }

            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(messageKey, 0, messageKey.size, "AES"), IvParameterSpec(payload.nonce))

            return cipher.doFinal(payload.ciphertext)
        } finally {
            messageKey?.fill(0)
        }
    }

    /**
     * Generates a pre-key bundle for session establishment.
     *
     * @param localUhid This node's UHID
     * @return PreKeyBundle for publishing to peers
     */
    fun generatePreKeyBundle(localUhid: String): PreKeyBundle {
        // Generate one-time pre-key (P-256)
        val preKeyBytes = generateP256PublicKey()
        val preKeyId = SecureRandom().nextInt(1, Int.MAX_VALUE)

        // Generate signed pre-key (P-256)
        val signedPreKeyBytes = generateP256PublicKey()
        val signedPreKeyId = SecureRandom().nextInt(1, Int.MAX_VALUE)

        // Sign the signed pre-key with Ed25519
        val signature = Ed25519Service.sign(ed25519PrivateKey, signedPreKeyBytes)

        return PreKeyBundle(
            uhid = localUhid,
            identityKey = ed25519PublicKey.clone(),
            preKeyId = preKeyId,
            preKey = preKeyBytes,
            signedPreKeyId = signedPreKeyId,
            signedPreKey = signedPreKeyBytes,
            signedPreKeySignature = signature
        )
    }

    /**
     * Processes a pre-key bundle and establishes a session.
     *
     * Verifies the bundle's signature and derives initial chain keys.
     *
     * @param bundle PreKeyBundle from the peer
     * @throws IllegalArgumentException if signature verification fails
     */
    fun processPreKeyBundle(bundle: PreKeyBundle) {
        // Verify the signed pre-key signature
        if (!Ed25519Service.verify(bundle.identityKey, bundle.signedPreKey, bundle.signedPreKeySignature)) {
            throw IllegalArgumentException("Signed pre-key signature verification failed.")
        }

        // Perform X3DH key agreement
        val sharedSecret = performECDH(bundle.signedPreKey, bundle.preKey)

        try {
            // Derive root key and initial chain keys using HKDF
            val rootKey = deriveKey(sharedSecret, AetherConstants.HKDF_ROOT_INFO)
            val sendChainKey = deriveKey(rootKey, AetherConstants.HKDF_CHAIN_SEND_INFO)
            val recvChainKey = deriveKey(rootKey, AetherConstants.HKDF_CHAIN_RECV_INFO)

            val session = SignalSession(
                rootKey = rootKey,
                sendChainKey = sendChainKey,
                recvChainKey = recvChainKey,
                remotePublicKey = bundle.identityKey.clone()
            )

            sessions[bundle.uhid] = session

            // Zero intermediate keys
            rootKey.fill(0)
        } finally {
            sharedSecret.fill(0)
        }
    }

    /**
     * Signs data using Ed25519.
     */
    fun signData(data: ByteArray): ByteArray = Ed25519Service.sign(ed25519PrivateKey, data)

    /**
     * Verifies a signature using Ed25519.
     */
    fun verifySignature(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean =
        Ed25519Service.verify(publicKey, data, signature)

    /**
     * Gets a copy of the Ed25519 public key.
     */
    fun getPublicKey(): ByteArray = ed25519PublicKey.clone()

    /**
     * Performs ECDH key agreement using generated P-256 keys.
     * Concatenates two DH results (similar to X3DH).
     */
    private fun performECDH(remoteSignedPreKey: ByteArray, remotePreKey: ByteArray): ByteArray {
        // Generate local ephemeral key
        val localKeyPair = generateP256KeyPair()

        // DH1: local <-> remote signed pre-key
        val dh1 = performDH(localKeyPair.first, remoteSignedPreKey)

        // DH2: local <-> remote pre-key
        val dh2 = performDH(localKeyPair.first, remotePreKey)

        return dh1 + dh2
    }

    /**
     * Performs a single ECDH key agreement.
     */
    private fun performDH(localPrivateKey: ByteArray, remotePublicKeyBytes: ByteArray): ByteArray {
        val keyFactory = KeyFactory.getInstance("EC")
        val publicKeySpec = X509EncodedKeySpec(remotePublicKeyBytes)
        val remotePublicKey = keyFactory.generatePublic(publicKeySpec)

        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(getPrivateKeyFromBytes(localPrivateKey))
        ka.doPhase(remotePublicKey, true)

        return ka.generateSecret()
    }

    /**
     * Derives a key using HKDF-SHA256.
     */
    private fun deriveKey(inputKeyMaterial: ByteArray, info: ByteArray): ByteArray {
        return hkdf(inputKeyMaterial, null, info, AetherConstants.AES_KEY_SIZE)
    }

    /**
     * Ratchets a chain key using HMAC-SHA256.
     * Returns (new chain key, message key).
     */
    private fun ratchetChainKey(chainKey: ByteArray, info: ByteArray): Pair<ByteArray, ByteArray> {
        // Message key = HKDF(chainKey, info, salt=0x01)
        val messageKey = hkdf(chainKey, byteArrayOf(0x01), info, AetherConstants.AES_KEY_SIZE)

        // New chain key = HKDF(chainKey, info, salt=0x02)
        val newChainKey = hkdf(chainKey, byteArrayOf(0x02), info, AetherConstants.AES_KEY_SIZE)

        return Pair(newChainKey, messageKey)
    }

    /**
     * HKDF (HMAC-based Key Derivation Function) using SHA-256.
     * Implements extract-expand KDF per RFC 5869.
     */
    private fun hkdf(ikm: ByteArray, salt: ByteArray?, info: ByteArray, length: Int): ByteArray {
        // Extract phase
        val actualSalt = salt ?: ByteArray(32) // Default salt is zeros
        val hmac = Mac.getInstance("HmacSHA256")
        hmac.init(SecretKeySpec(actualSalt, "HmacSHA256"))
        val prk = hmac.doFinal(ikm)

        // Expand phase
        val result = mutableListOf<Byte>()
        var counter = 1
        var t = ByteArray(0)

        while (result.size < length) {
            val hmacExpand = Mac.getInstance("HmacSHA256")
            hmacExpand.init(SecretKeySpec(prk, "HmacSHA256"))
            hmacExpand.update(t)
            hmacExpand.update(info)
            hmacExpand.update(counter.toByte())
            t = hmacExpand.doFinal()
            result.addAll(t.take(min(t.size, length - result.size)))
            counter++
        }

        return result.take(length).toByteArray()
    }

    /**
     * Generates a P-256 key pair, returning (privateKeyBytes, publicKeyBytes).
     */
    private fun generateP256KeyPair(): Pair<ByteArray, ByteArray> {
        val keyFactory = KeyFactory.getInstance("EC")
        val kpg = java.security.KeyPairGenerator.getInstance("EC")
        kpg.initialize(java.security.spec.ECGenParameterSpec("secp256r1"))
        val keyPair = kpg.generateKeyPair()

        // Extract as bytes - simplified representation
        val privateKey = keyPair.private.encoded
        val publicKey = keyPair.public.encoded

        return Pair(privateKey, publicKey)
    }

    /**
     * Generates a P-256 public key only, returning bytes.
     */
    private fun generateP256PublicKey(): ByteArray {
        val kpg = java.security.KeyPairGenerator.getInstance("EC")
        kpg.initialize(java.security.spec.ECGenParameterSpec("secp256r1"))
        val keyPair = kpg.generateKeyPair()
        return keyPair.public.encoded
    }

    /**
     * Reconstructs a PrivateKey from bytes.
     */
    private fun getPrivateKeyFromBytes(keyBytes: ByteArray): java.security.PrivateKey {
        val keyFactory = KeyFactory.getInstance("EC")
        val keySpec = java.security.spec.PKCS8EncodedKeySpec(keyBytes)
        return keyFactory.generatePrivate(keySpec)
    }
}

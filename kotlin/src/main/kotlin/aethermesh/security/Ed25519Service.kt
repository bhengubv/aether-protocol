// SPDX-License-Identifier: MIT

package aethermesh.security

import org.bouncycastle.crypto.AsymmetricCipherKeyPair
import org.bouncycastle.crypto.generators.Ed25519KeyPairGenerator
import org.bouncycastle.crypto.params.Ed25519KeyGenerationParameters
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import java.security.SecureRandom

/**
 * Ed25519 signing service using BouncyCastle.
 * Key format: 32-byte private key, 32-byte public key, 64-byte signature.
 *
 * Provides key generation, signing, and verification compatible with the C# implementation.
 */
object Ed25519Service {
    const val PRIVATE_KEY_SIZE = 32
    const val PUBLIC_KEY_SIZE = 32
    const val SIGNATURE_SIZE = 64

    /**
     * Generates a new Ed25519 key pair.
     *
     * @return A pair of (privateKey: 32-byte seed, publicKey: 32-byte point)
     */
    fun generateKeyPair(): Pair<ByteArray, ByteArray> {
        val generator = Ed25519KeyPairGenerator()
        generator.init(Ed25519KeyGenerationParameters(SecureRandom()))

        val keyPair = generator.generateKeyPair()
        val privateKey = (keyPair.private as Ed25519PrivateKeyParameters).encoded
        val publicKey = (keyPair.public as Ed25519PublicKeyParameters).encoded

        return Pair(privateKey, publicKey)
    }

    /**
     * Signs data using an Ed25519 private key.
     *
     * @param privateKey 32-byte Ed25519 private key
     * @param data The data to sign
     * @return 64-byte Ed25519 signature
     */
    fun sign(privateKey: ByteArray, data: ByteArray): ByteArray {
        require(privateKey.size == PRIVATE_KEY_SIZE) {
            "Ed25519 private key must be $PRIVATE_KEY_SIZE bytes"
        }

        val signer = Ed25519Signer()
        signer.init(true, Ed25519PrivateKeyParameters(privateKey, 0))
        signer.update(data, 0, data.size)

        return signer.generateSignature()
    }

    /**
     * Verifies an Ed25519 signature.
     *
     * @param publicKey 32-byte Ed25519 public key
     * @param data The signed data
     * @param signature 64-byte Ed25519 signature
     * @return True if the signature is valid
     */
    fun verify(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean {
        if (publicKey.size != PUBLIC_KEY_SIZE) return false
        if (signature.size != SIGNATURE_SIZE) return false

        return try {
            val signer = Ed25519Signer()
            signer.init(false, Ed25519PublicKeyParameters(publicKey, 0))
            signer.update(data, 0, data.size)

            signer.verifySignature(signature)
        } catch (e: Exception) {
            false
        }
    }
}

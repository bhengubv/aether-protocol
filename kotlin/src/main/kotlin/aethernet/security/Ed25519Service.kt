// SPDX-License-Identifier: MIT

package aethernet.security

import org.bouncycastle.crypto.AsymmetricCipherKeyPair
import org.bouncycastle.crypto.generators.Ed25519KeyPairGenerator
import org.bouncycastle.crypto.params.Ed25519KeyGenerationParameters
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import java.security.KeyFactory
import java.security.SecureRandom
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec

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

    /**
     * Verifies a signature, trying Ed25519 first and falling back to legacy P-256
     * ECDSA for public keys longer than 32 bytes (Protocol Version 1 identity keys
     * during the migration window — see PROTOCOL_SPEC.md §7.5).
     *
     * A 32-byte key takes the Ed25519 path; a longer key is a DER SubjectPublicKeyInfo
     * P-256 key verified against an ASN.1 DER ECDSA signature over SHA-256.
     */
    fun verifyWithFallback(publicKey: ByteArray, data: ByteArray, signature: ByteArray): Boolean {
        return if (publicKey.size == PUBLIC_KEY_SIZE) {
            verify(publicKey, data, signature)
        } else {
            verifyP256(publicKey, data, signature)
        }
    }

    /**
     * Verifies a legacy P-256 (secp256r1) ECDSA signature over SHA-256 using the JDK's
     * standard providers. Public key is X.509 SubjectPublicKeyInfo (DER); signature is
     * ASN.1 DER.
     */
    private fun verifyP256(spkiPublicKey: ByteArray, data: ByteArray, derSignature: ByteArray): Boolean {
        return try {
            val pubKey = KeyFactory.getInstance("EC")
                .generatePublic(X509EncodedKeySpec(spkiPublicKey)) as ECPublicKey
            // Guard: must be the 256-bit P-256 curve.
            if (pubKey.params.curve.field.fieldSize != 256) return false
            val verifier = Signature.getInstance("SHA256withECDSA")
            verifier.initVerify(pubKey)
            verifier.update(data)
            verifier.verify(derSignature)
        } catch (e: Exception) {
            false
        }
    }
}

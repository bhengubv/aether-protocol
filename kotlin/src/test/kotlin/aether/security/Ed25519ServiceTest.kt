// SPDX-License-Identifier: MIT

package aether.security

import org.junit.jupiter.api.Test
import kotlin.test.*

class Ed25519ServiceTest {

    // ── generateKeyPair ───────────────────────────────────────────────────────

    @Test fun `generateKeyPair returns 32-byte private key`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        assertEquals(Ed25519Service.PRIVATE_KEY_SIZE, privateKey.size)
    }

    @Test fun `generateKeyPair returns 32-byte public key`() {
        val (_, publicKey) = Ed25519Service.generateKeyPair()
        assertEquals(Ed25519Service.PUBLIC_KEY_SIZE, publicKey.size)
    }

    @Test fun `generateKeyPair returns unique keys on each call`() {
        val (priv1, pub1) = Ed25519Service.generateKeyPair()
        val (priv2, pub2) = Ed25519Service.generateKeyPair()
        assertFalse(priv1.contentEquals(priv2), "private keys should differ")
        assertFalse(pub1.contentEquals(pub2), "public keys should differ")
    }

    @Test fun `generateKeyPair returns non-zero private key`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        assertTrue(privateKey.any { it != 0.toByte() }, "private key should not be all zeros")
    }

    @Test fun `generateKeyPair returns non-zero public key`() {
        val (_, publicKey) = Ed25519Service.generateKeyPair()
        assertTrue(publicKey.any { it != 0.toByte() }, "public key should not be all zeros")
    }

    // ── sign ─────────────────────────────────────────────────────────────────

    @Test fun `sign returns 64-byte signature`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val sig = Ed25519Service.sign(privateKey, "hello aether".toByteArray())
        assertEquals(Ed25519Service.SIGNATURE_SIZE, sig.size)
    }

    @Test fun `sign throws for wrong-length private key`() {
        assertFailsWith<IllegalArgumentException> {
            Ed25519Service.sign(ByteArray(16), "data".toByteArray())
        }
    }

    @Test fun `sign produces different signatures for different data`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val sig1 = Ed25519Service.sign(privateKey, "msg-a".toByteArray())
        val sig2 = Ed25519Service.sign(privateKey, "msg-b".toByteArray())
        assertFalse(sig1.contentEquals(sig2))
    }

    @Test fun `sign is deterministic — same key and data produce same signature`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val data = "same data".toByteArray()
        val sig1 = Ed25519Service.sign(privateKey, data)
        val sig2 = Ed25519Service.sign(privateKey, data)
        assertTrue(sig1.contentEquals(sig2), "Ed25519 should be deterministic")
    }

    @Test fun `sign works with empty data`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val sig = Ed25519Service.sign(privateKey, byteArrayOf())
        assertEquals(Ed25519Service.SIGNATURE_SIZE, sig.size)
    }

    @Test fun `sign works with large data`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val bigData = ByteArray(64 * 1024) { (it and 0xFF).toByte() }
        val sig = Ed25519Service.sign(privateKey, bigData)
        assertEquals(Ed25519Service.SIGNATURE_SIZE, sig.size)
    }

    // ── verify ────────────────────────────────────────────────────────────────

    @Test fun `verify returns true for valid signature`() {
        val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        val data = "hello world".toByteArray()
        val sig = Ed25519Service.sign(privateKey, data)
        assertTrue(Ed25519Service.verify(publicKey, data, sig))
    }

    @Test fun `verify returns false for tampered data`() {
        val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        val data = "original".toByteArray()
        val sig = Ed25519Service.sign(privateKey, data)
        val tampered = "tampered".toByteArray()
        assertFalse(Ed25519Service.verify(publicKey, tampered, sig))
    }

    @Test fun `verify returns false for tampered signature`() {
        val (privateKey, publicKey) = Ed25519Service.generateKeyPair()
        val data = "data".toByteArray()
        val sig = Ed25519Service.sign(privateKey, data).copyOf()
        sig[0] = (sig[0].toInt() xor 0xFF).toByte() // flip bits
        assertFalse(Ed25519Service.verify(publicKey, data, sig))
    }

    @Test fun `verify returns false for wrong public key`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val (_, wrongPublicKey) = Ed25519Service.generateKeyPair()
        val data = "data".toByteArray()
        val sig = Ed25519Service.sign(privateKey, data)
        assertFalse(Ed25519Service.verify(wrongPublicKey, data, sig))
    }

    @Test fun `verify returns false for wrong-length public key`() {
        val (privateKey, _) = Ed25519Service.generateKeyPair()
        val data = "data".toByteArray()
        val sig = Ed25519Service.sign(privateKey, data)
        assertFalse(Ed25519Service.verify(ByteArray(16), data, sig))
    }

    @Test fun `verify returns false for wrong-length signature`() {
        val (_, publicKey) = Ed25519Service.generateKeyPair()
        assertFalse(Ed25519Service.verify(publicKey, "data".toByteArray(), ByteArray(32)))
    }

    @Test fun `verify returns false for all-zero signature`() {
        val (_, publicKey) = Ed25519Service.generateKeyPair()
        val data = "data".toByteArray()
        assertFalse(Ed25519Service.verify(publicKey, data, ByteArray(64)))
    }

    @Test fun `sign and verify round-trip works for multiple key pairs`() {
        repeat(5) {
            val (priv, pub) = Ed25519Service.generateKeyPair()
            val data = "message $it".toByteArray()
            val sig = Ed25519Service.sign(priv, data)
            assertTrue(Ed25519Service.verify(pub, data, sig), "round-trip failed for iteration $it")
        }
    }

    @Test fun `verify returns false for cross-signed data`() {
        val (priv1, _) = Ed25519Service.generateKeyPair()
        val (_, pub2) = Ed25519Service.generateKeyPair()
        val data = "data".toByteArray()
        val sig = Ed25519Service.sign(priv1, data)
        // pub2 should reject sig made by priv1
        assertFalse(Ed25519Service.verify(pub2, data, sig))
    }

    // ── constants ─────────────────────────────────────────────────────────────

    @Test fun `PRIVATE_KEY_SIZE is 32`() {
        assertEquals(32, Ed25519Service.PRIVATE_KEY_SIZE)
    }

    @Test fun `PUBLIC_KEY_SIZE is 32`() {
        assertEquals(32, Ed25519Service.PUBLIC_KEY_SIZE)
    }

    @Test fun `SIGNATURE_SIZE is 64`() {
        assertEquals(64, Ed25519Service.SIGNATURE_SIZE)
    }
}

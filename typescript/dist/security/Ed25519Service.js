/**
 * Ed25519 signing service using TweetNaCl
 * SPDX-License-Identifier: MIT
 */
import nacl from "tweetnacl";
/**
 * Ed25519 signing service using TweetNaCl/libsodium
 * Key format: 32-byte seed (private), 32-byte point (public), 64-byte signature
 */
export class Ed25519Service {
    /**
     * Generate a new Ed25519 key pair
     * @returns {Ed25519KeyPair} Tuple of (privateKey, publicKey)
     */
    static generateKeyPair() {
        const keyPair = nacl.sign.keyPair();
        return {
            privateKey: keyPair.secretKey.slice(0, 32),
            publicKey: keyPair.publicKey,
        };
    }
    /**
     * Sign data using an Ed25519 private key
     * @param privateKey - 32-byte Ed25519 seed
     * @param data - The data to sign
     * @returns 64-byte Ed25519 signature
     */
    static sign(privateKey, data) {
        if (privateKey.length !== 32) {
            throw new Error("Ed25519 private key must be 32 bytes");
        }
        // TweetNaCl expects a 64-byte secret key (32 seed + 32 public key)
        // We reconstruct it from the seed
        const keyPair = nacl.sign.keyPair.fromSeed(privateKey);
        const signed = nacl.sign(data, keyPair.secretKey);
        // Extract just the signature (first 64 bytes)
        return signed.slice(0, 64);
    }
    /**
     * Verify an Ed25519 signature
     * @param publicKey - 32-byte Ed25519 public key
     * @param data - The signed data
     * @param signature - 64-byte Ed25519 signature
     * @returns true if the signature is valid
     */
    static verify(publicKey, data, signature) {
        if (publicKey.length !== 32) {
            return false;
        }
        if (signature.length !== 64) {
            return false;
        }
        try {
            // Combine signature + data for TweetNaCl verification
            const signedMessage = new Uint8Array(64 + data.length);
            signedMessage.set(signature, 0);
            signedMessage.set(data, 64);
            const opened = nacl.sign.open(signedMessage, publicKey);
            return opened !== null;
        }
        catch {
            return false;
        }
    }
    /**
     * Verify a signature with fallback support for legacy P-256 keys
     * Currently only supports Ed25519 (P-256 fallback not implemented in TS version)
     * @param publicKey - Public key bytes (32 = Ed25519)
     * @param data - The signed data
     * @param signature - The signature bytes
     * @returns true if the signature is valid
     */
    static verifyWithFallback(publicKey, data, signature) {
        // Standard Ed25519 path
        if (publicKey.length === 32) {
            return this.verify(publicKey, data, signature);
        }
        // Legacy P-256 path would go here, but not implemented in TS version
        return false;
    }
}
//# sourceMappingURL=Ed25519Service.js.map
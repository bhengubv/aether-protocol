/**
 * Ed25519 signing service using TweetNaCl
 * SPDX-License-Identifier: MIT
 */
export interface Ed25519KeyPair {
    privateKey: Uint8Array;
    publicKey: Uint8Array;
}
/**
 * Ed25519 signing service using TweetNaCl/libsodium
 * Key format: 32-byte seed (private), 32-byte point (public), 64-byte signature
 */
export declare class Ed25519Service {
    /**
     * Generate a new Ed25519 key pair
     * @returns {Ed25519KeyPair} Tuple of (privateKey, publicKey)
     */
    static generateKeyPair(): Ed25519KeyPair;
    /**
     * Sign data using an Ed25519 private key
     * @param privateKey - 32-byte Ed25519 seed
     * @param data - The data to sign
     * @returns 64-byte Ed25519 signature
     */
    static sign(privateKey: Uint8Array, data: Uint8Array): Uint8Array;
    /**
     * Verify an Ed25519 signature
     * @param publicKey - 32-byte Ed25519 public key
     * @param data - The signed data
     * @param signature - 64-byte Ed25519 signature
     * @returns true if the signature is valid
     */
    static verify(publicKey: Uint8Array, data: Uint8Array, signature: Uint8Array): boolean;
    /**
     * Verify a signature with fallback support for legacy P-256 keys
     * Currently only supports Ed25519 (P-256 fallback not implemented in TS version)
     * @param publicKey - Public key bytes (32 = Ed25519)
     * @param data - The signed data
     * @param signature - The signature bytes
     * @returns true if the signature is valid
     */
    static verifyWithFallback(publicKey: Uint8Array, data: Uint8Array, signature: Uint8Array): boolean;
}
//# sourceMappingURL=Ed25519Service.d.ts.map
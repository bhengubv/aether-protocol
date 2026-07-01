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
     * Verify a signature, trying Ed25519 first and falling back to legacy P-256 ECDSA
     * for public keys longer than 32 bytes (Protocol Version 1 identity keys during the
     * migration window — see PROTOCOL_SPEC.md §7.5). The longer key is a DER
     * SubjectPublicKeyInfo P-256 key verified against an ASN.1 DER ECDSA signature
     * over SHA-256.
     * @param publicKey - 32-byte Ed25519 key, or a DER SPKI P-256 key (> 32 bytes)
     * @param data - The signed data
     * @param signature - 64-byte Ed25519 signature, or an ASN.1 DER ECDSA signature
     * @returns true if the signature is valid under whichever scheme the key selects
     */
    static verifyWithFallback(publicKey: Uint8Array, data: Uint8Array, signature: Uint8Array): boolean;
    /**
     * Verify a legacy P-256 (secp256r1) ECDSA signature over SHA-256.
     * Public key is X.509 SubjectPublicKeyInfo (DER); signature is ASN.1 DER.
     */
    private static verifyP256;
}
//# sourceMappingURL=Ed25519Service.d.ts.map
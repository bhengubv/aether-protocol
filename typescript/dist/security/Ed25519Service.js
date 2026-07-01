/**
 * Ed25519 signing service using TweetNaCl
 * SPDX-License-Identifier: MIT
 */
import nacl from "tweetnacl";
import { createPublicKey, verify as nodeVerify } from "crypto";
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
    static verifyWithFallback(publicKey, data, signature) {
        if (publicKey.length === 32) {
            return this.verify(publicKey, data, signature);
        }
        return this.verifyP256(publicKey, data, signature);
    }
    /**
     * Verify a legacy P-256 (secp256r1) ECDSA signature over SHA-256.
     * Public key is X.509 SubjectPublicKeyInfo (DER); signature is ASN.1 DER.
     */
    static verifyP256(spkiPublicKey, data, derSignature) {
        try {
            const key = createPublicKey({
                key: Buffer.from(spkiPublicKey),
                format: "der",
                type: "spki",
            });
            const details = key.asymmetricKeyDetails;
            if (key.asymmetricKeyType !== "ec" || details?.namedCurve !== "prime256v1") {
                return false;
            }
            return nodeVerify("sha256", Buffer.from(data), { key, dsaEncoding: "der" }, Buffer.from(derSignature));
        }
        catch {
            return false;
        }
    }
}
//# sourceMappingURL=Ed25519Service.js.map
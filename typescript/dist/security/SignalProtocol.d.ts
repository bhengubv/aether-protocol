/**
 * Signal Protocol implementation for end-to-end encryption
 * X3DH key exchange with P-256 ECDH, HKDF-SHA256 key derivation, AES-256-GCM
 * SPDX-License-Identifier: MIT
 */
export interface PreKeyBundle {
    uhid: string;
    identityKey: Uint8Array;
    preKeyId: number;
    preKey: Uint8Array;
    signedPreKeyId: number;
    signedPreKey: Uint8Array;
    signedPreKeySignature: Uint8Array;
}
export interface EncryptedPayload {
    ciphertext: Uint8Array;
    nonce: Uint8Array;
    messageType: number;
    senderUhid: string;
    counter: number;
    encryptedAt: Date;
}
/**
 * Signal Protocol implementation
 * Note: This version uses Node.js crypto for ECDH (P-256)
 * For production use, consider integrating libsodium or similar for X25519
 */
export declare class SignalProtocol {
    private sessions;
    private identityPrivateKey;
    private identityPublicKey;
    private ed25519PrivateKey;
    private ed25519PublicKey;
    constructor();
    /**
     * Check if a session exists with a peer
     */
    hasSession(peerUhid: string): boolean;
    /**
     * Encrypt plaintext for a peer (requires active session)
     */
    encrypt(peerUhid: string, plaintext: Uint8Array): Promise<EncryptedPayload>;
    /**
     * Decrypt ciphertext from a peer (requires active session)
     */
    decrypt(peerUhid: string, payload: EncryptedPayload): Promise<Uint8Array>;
    /**
     * Generate a pre-key bundle for asynchronous session establishment
     */
    generatePreKeyBundle(localUhid: string): Promise<PreKeyBundle>;
    /**
     * Process a pre-key bundle and establish a session
     */
    processPreKeyBundle(bundle: PreKeyBundle): Promise<void>;
    /**
     * Get the Ed25519 public key
     */
    getPublicKey(): Uint8Array;
    /**
     * Derive a 32-byte key using HKDF-SHA256
     */
    private deriveKey;
    /**
     * Ratchet chain key to derive message key
     * Derives a 32-byte message key from the current chain key
     */
    private ratchetChainKeyForEncrypt;
    /**
     * Ratchet chain key to advance it
     * Returns new chain key after advancement
     */
    private ratchetChainKeyForAdvance;
}
//# sourceMappingURL=SignalProtocol.d.ts.map
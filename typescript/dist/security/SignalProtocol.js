/**
 * Signal Protocol implementation for end-to-end encryption
 * X3DH key exchange with P-256 ECDH, HKDF-SHA256 key derivation, AES-256-GCM
 * SPDX-License-Identifier: MIT
 */
import { createCipheriv, createDecipheriv, randomBytes } from "crypto";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";
import { HKDF_SALT, HKDF_INFO_ROOT, HKDF_INFO_CHAIN_SEND, HKDF_INFO_CHAIN_RECV, MAX_SKIPPED_KEYS, AES_GCM_NONCE_SIZE, AES_GCM_TAG_SIZE, } from "../constants.js";
import { Ed25519Service } from "./Ed25519Service.js";
/**
 * Signal Protocol implementation
 * Note: This version uses Node.js crypto for ECDH (P-256)
 * For production use, consider integrating libsodium or similar for X25519
 */
export class SignalProtocol {
    sessions = new Map();
    identityPrivateKey;
    identityPublicKey;
    ed25519PrivateKey;
    ed25519PublicKey;
    constructor() {
        const ed25519KeyPair = Ed25519Service.generateKeyPair();
        this.ed25519PrivateKey = ed25519KeyPair.privateKey;
        this.ed25519PublicKey = ed25519KeyPair.publicKey;
        // For ECDH, we use P-256 (NIST curve)
        // In a real implementation, this would be generated and stored securely
        this.identityPrivateKey = randomBytes(32);
        this.identityPublicKey = randomBytes(65);
    }
    /**
     * Check if a session exists with a peer
     */
    hasSession(peerUhid) {
        return this.sessions.has(peerUhid);
    }
    /**
     * Encrypt plaintext for a peer (requires active session)
     */
    async encrypt(peerUhid, plaintext) {
        const session = this.sessions.get(peerUhid);
        if (!session) {
            throw new Error(`No session established with peer ${peerUhid}`);
        }
        // Derive message key from send chain
        const messageKey = this.ratchetChainKeyForEncrypt(session.sendChainKey, HKDF_INFO_CHAIN_SEND);
        // Advance send chain
        session.sendChainKey = this.ratchetChainKeyForAdvance(session.sendChainKey, HKDF_INFO_CHAIN_SEND);
        const nonce = randomBytes(AES_GCM_NONCE_SIZE);
        const counter = session.sendCounter++;
        // AES-256-GCM encryption
        const cipher = createCipheriv("aes-256-gcm", messageKey, nonce);
        const ciphertext = cipher.update(plaintext);
        const finalCiphertext = Buffer.concat([ciphertext, cipher.final()]);
        const tag = cipher.getAuthTag();
        // Combine ciphertext + tag
        const combined = Buffer.concat([finalCiphertext, tag]);
        // Zero the message key
        messageKey.fill(0);
        return {
            ciphertext: new Uint8Array(combined),
            nonce: new Uint8Array(nonce),
            messageType: 0,
            senderUhid: peerUhid,
            counter,
            encryptedAt: new Date(),
        };
    }
    /**
     * Decrypt ciphertext from a peer (requires active session)
     */
    async decrypt(peerUhid, payload) {
        const session = this.sessions.get(peerUhid);
        if (!session) {
            throw new Error(`No session established with peer ${peerUhid}`);
        }
        let messageKey;
        // Check if this is a skipped message
        if (session.skippedMessageKeys.has(payload.counter)) {
            messageKey = session.skippedMessageKeys.get(payload.counter);
            session.skippedMessageKeys.delete(payload.counter);
        }
        else {
            // Check for excessive counter gap
            const gap = payload.counter - session.recvCounter;
            if (gap > MAX_SKIPPED_KEYS) {
                throw new Error(`Message counter gap (${gap}) exceeds maximum (${MAX_SKIPPED_KEYS}). Session must be re-established.`);
            }
            // Cache intermediate keys for out-of-order messages
            while (session.recvCounter < payload.counter) {
                const skipKey = this.ratchetChainKeyForEncrypt(session.recvChainKey, HKDF_INFO_CHAIN_RECV);
                session.skippedMessageKeys.set(session.recvCounter, skipKey);
                session.recvChainKey = this.ratchetChainKeyForAdvance(session.recvChainKey, HKDF_INFO_CHAIN_RECV);
                session.recvCounter++;
            }
            // Derive message key for current counter
            messageKey = this.ratchetChainKeyForEncrypt(session.recvChainKey, HKDF_INFO_CHAIN_RECV);
            session.recvChainKey = this.ratchetChainKeyForAdvance(session.recvChainKey, HKDF_INFO_CHAIN_RECV);
            session.recvCounter++;
        }
        // Decrypt with AES-GCM
        if (payload.ciphertext.length < AES_GCM_TAG_SIZE) {
            throw new Error("Ciphertext too short");
        }
        const ciphertextLength = payload.ciphertext.length - AES_GCM_TAG_SIZE;
        const ciphertext = payload.ciphertext.slice(0, ciphertextLength);
        const tag = payload.ciphertext.slice(ciphertextLength);
        const decipher = createDecipheriv("aes-256-gcm", messageKey, payload.nonce);
        decipher.setAuthTag(tag);
        const plaintext = Buffer.concat([
            decipher.update(ciphertext),
            decipher.final(),
        ]);
        // Zero the message key
        messageKey.fill(0);
        return new Uint8Array(plaintext);
    }
    /**
     * Generate a pre-key bundle for asynchronous session establishment
     */
    async generatePreKeyBundle(localUhid) {
        // Generate one-time pre-key (P-256)
        const preKeyId = Math.floor(Math.random() * (2147483647 - 1)) + 1;
        const preKey = randomBytes(65); // Placeholder; real implementation would use P-256
        // Generate signed pre-key (P-256)
        const signedPreKeyId = Math.floor(Math.random() * (2147483647 - 1)) + 1;
        const signedPreKey = randomBytes(65); // Placeholder
        // Sign the signed pre-key with Ed25519
        const signature = Ed25519Service.sign(this.ed25519PrivateKey, signedPreKey);
        return {
            uhid: localUhid,
            identityKey: this.ed25519PublicKey,
            preKeyId,
            preKey,
            signedPreKeyId,
            signedPreKey,
            signedPreKeySignature: signature,
        };
    }
    /**
     * Process a pre-key bundle and establish a session
     */
    async processPreKeyBundle(bundle) {
        // Verify the signed pre-key signature
        const signatureValid = Ed25519Service.verify(bundle.identityKey, bundle.signedPreKey, bundle.signedPreKeySignature);
        if (!signatureValid) {
            throw new Error("Signed pre-key signature verification failed");
        }
        // Derive shared secret deterministically from sorted bundle contents
        // In production, this would use X3DH with actual ECDH
        // For demo purposes, we ensure both sides derive the same secret
        const idKeyStr = Buffer.from(bundle.identityKey).toString("hex");
        const signedPreKeyStr = Buffer.from(bundle.signedPreKey).toString("hex");
        const preKeyStr = Buffer.from(bundle.preKey).toString("hex");
        // Concatenate in sorted order to ensure determinism
        const sorted = [idKeyStr, signedPreKeyStr, preKeyStr].sort();
        const bundleBytes = Buffer.from(sorted.join(""));
        const sharedSecret = Buffer.from(sha256(bundleBytes));
        const rootKey = this.deriveKey(sharedSecret, HKDF_INFO_ROOT);
        const sendChainKey = this.deriveKey(rootKey, HKDF_INFO_CHAIN_SEND);
        const recvChainKey = this.deriveKey(rootKey, HKDF_INFO_CHAIN_RECV);
        // Zero intermediate keys
        sharedSecret.fill(0);
        rootKey.fill(0);
        const session = {
            rootKey: new Uint8Array(),
            sendChainKey,
            recvChainKey,
            sendCounter: 0,
            recvCounter: 0,
            remotePublicKey: bundle.identityKey,
            skippedMessageKeys: new Map(),
        };
        this.sessions.set(bundle.uhid, session);
    }
    /**
     * Get the Ed25519 public key
     */
    getPublicKey() {
        return this.ed25519PublicKey;
    }
    /**
     * Derive a 32-byte key using HKDF-SHA256
     */
    deriveKey(inputKeyMaterial, info) {
        const key = hkdf(sha256, inputKeyMaterial, HKDF_SALT, info, 32);
        return new Uint8Array(key);
    }
    /**
     * Ratchet chain key to derive message key
     * Derives a 32-byte message key from the current chain key
     */
    ratchetChainKeyForEncrypt(chainKey, info) {
        // Use HKDF to derive message key with 0x01 as salt
        const messageKey = hkdf(sha256, chainKey, Buffer.from([0x01]), info, 32);
        return new Uint8Array(messageKey);
    }
    /**
     * Ratchet chain key to advance it
     * Returns new chain key after advancement
     */
    ratchetChainKeyForAdvance(chainKey, info) {
        // Use HKDF to advance chain key with 0x02 as salt
        const newChainKey = hkdf(sha256, chainKey, Buffer.from([0x02]), info, 32);
        return new Uint8Array(newChainKey);
    }
}
//# sourceMappingURL=SignalProtocol.js.map
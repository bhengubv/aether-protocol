// SPDX-License-Identifier: MIT

use crate::constants::*;
use crate::models::{EncryptedPayload, PreKeyBundle, SignalSession};
use aes_gcm::{aead::Aead, Aes256Gcm, Key, Nonce};
use ed25519_dalek::{SigningKey, VerifyingKey};
use hkdf::Hkdf;
use hmac::{Hmac, Mac};
use rand::Rng;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use x25519_dalek::{EphemeralSecret, PublicKey as X25519PublicKey, StaticSecret};

type HmacSha256 = Hmac<Sha256>;

/// Signal Protocol implementation for Aether mesh networking
pub struct SignalProtocolService {
    identity_private_key: Vec<u8>, // 32-byte Ed25519 seed
    identity_public_key: Vec<u8>,  // 32-byte Ed25519 public key
    sessions: HashMap<String, SignalSession>,
}

impl SignalProtocolService {
    /// Creates a new Signal Protocol service with generated identity keys
    pub fn new() -> Self {
        let (private_key, public_key) = crate::security::Ed25519SigningService::generate_keypair();

        SignalProtocolService {
            identity_private_key: private_key,
            identity_public_key: public_key,
            sessions: HashMap::new(),
        }
    }

    /// Returns true if a session exists with the peer
    pub fn has_session(&self, peer_uhid: &str) -> bool {
        self.sessions.contains_key(peer_uhid)
    }

    /// Generates a pre-key bundle for publishing to the network
    pub fn generate_pre_key_bundle(&self, local_uhid: &str) -> Result<PreKeyBundle, Box<dyn std::error::Error>> {
        let mut rng = rand::thread_rng();

        // Generate one-time pre-key (X25519)
        let pre_key_secret = StaticSecret::random_from_rng(&mut rng);
        let pre_key_public = X25519PublicKey::from(&pre_key_secret);
        let pre_key_id = rng.gen_range(1..i32::MAX);

        // Generate signed pre-key (X25519)
        let signed_pre_key_secret = StaticSecret::random_from_rng(&mut rng);
        let signed_pre_key_public = X25519PublicKey::from(&signed_pre_key_secret);
        let signed_pre_key_id = rng.gen_range(1..i32::MAX);

        // Sign the signed pre-key with our Ed25519 identity key
        let signature = crate::security::Ed25519SigningService::sign(
            &self.identity_private_key,
            signed_pre_key_public.as_bytes(),
        )?;

        Ok(PreKeyBundle::new(
            local_uhid.to_string(),
            self.identity_public_key.clone(),
            pre_key_id,
            pre_key_public.as_bytes().to_vec(),
            signed_pre_key_id,
            signed_pre_key_public.as_bytes().to_vec(),
            signature,
        ))
    }

    /// Processes a pre-key bundle and establishes a session
    pub fn process_pre_key_bundle(&mut self, bundle: &PreKeyBundle) -> Result<(), Box<dyn std::error::Error>> {
        // Verify the signed pre-key signature
        if !crate::security::Ed25519SigningService::verify(
            &bundle.identity_key,
            &bundle.signed_pre_key,
            &bundle.signed_pre_key_signature,
        ) {
            return Err("Signed pre-key signature verification failed".into());
        }

        // Perform X3DH key agreement
        let shared_secret = self.perform_x3dh(&bundle.signed_pre_key, &bundle.pre_key)?;

        // Derive keys using HKDF-SHA256
        let hk = Hkdf::<Sha256>::new(Some(HKDF_SALT), &shared_secret);

        let mut root_key = vec![0u8; AES_KEY_SIZE];
        hk.expand(HKDF_ROOT_INFO, &mut root_key)?;

        let mut send_chain_key = vec![0u8; AES_KEY_SIZE];
        hk.expand(HKDF_CHAIN_SEND_INFO, &mut send_chain_key)?;

        let mut recv_chain_key = vec![0u8; AES_KEY_SIZE];
        hk.expand(HKDF_CHAIN_RECV_INFO, &mut recv_chain_key)?;

        // Zero the shared secret
        let mut secret_copy = shared_secret;
        for byte in &mut secret_copy {
            *byte = 0;
        }

        // Create and store the session
        let mut session = SignalSession::new(bundle.uhid.clone(), bundle.identity_key.clone());
        session.root_key = root_key;
        session.send_chain_key = send_chain_key;
        session.recv_chain_key = recv_chain_key;

        self.sessions.insert(bundle.uhid.clone(), session);

        Ok(())
    }

    /// Encrypts plaintext for a peer using the established session
    pub fn encrypt(&mut self, peer_uhid: &str, plaintext: &[u8]) -> Result<EncryptedPayload, Box<dyn std::error::Error>> {
        let session = self
            .sessions
            .get_mut(peer_uhid)
            .ok_or("No session established with peer")?;

        // Ratchet the sending chain
        let (new_chain_key, message_key) = self.ratchet_chain_key(&session.send_chain_key, HKDF_CHAIN_SEND_INFO)?;
        session.send_chain_key = new_chain_key;

        // Encrypt with AES-256-GCM
        let mut rng = rand::thread_rng();
        let nonce_bytes = {
            let mut n = [0u8; AES_GCM_NONCE_SIZE];
            rng.fill(&mut n);
            n
        };

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&message_key));
        let nonce = Nonce::from_slice(&nonce_bytes);

        let ciphertext = cipher
            .encrypt(nonce, plaintext)
            .map_err(|e| format!("Encryption failed: {}", e))?;

        // Zero the message key
        let mut key_copy = message_key;
        for byte in &mut key_copy {
            *byte = 0;
        }

        let counter = session.send_counter;
        session.send_counter += 1;

        Ok(EncryptedPayload::new(
            ciphertext,
            nonce_bytes.to_vec(),
            0,
            peer_uhid.to_string(),
            counter,
        ))
    }

    /// Decrypts an encrypted payload using the established session
    pub fn decrypt(&mut self, peer_uhid: &str, payload: &EncryptedPayload) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
        let session = self
            .sessions
            .get_mut(peer_uhid)
            .ok_or("No session established with peer")?;

        let message_key = if let Some(skipped_key) = session.skipped_message_keys.remove(&payload.counter) {
            skipped_key
        } else {
            let gap = payload.counter as i32 - session.recv_counter as i32;

            if gap > MAX_SKIPPED_KEYS as i32 {
                return Err(format!(
                    "Message counter gap ({}) exceeds maximum ({}). Session must be re-established.",
                    gap, MAX_SKIPPED_KEYS
                )
                .into());
            }

            // Cache skipped keys
            while session.recv_counter < payload.counter {
                let (new_chain_key, skip_key) =
                    self.ratchet_chain_key(&session.recv_chain_key, HKDF_CHAIN_RECV_INFO)?;
                session.recv_chain_key = new_chain_key;
                session.skipped_message_keys.insert(session.recv_counter, skip_key);
                session.recv_counter += 1;
            }

            // Derive the actual message key
            let (new_chain_key, mk) = self.ratchet_chain_key(&session.recv_chain_key, HKDF_CHAIN_RECV_INFO)?;
            session.recv_chain_key = new_chain_key;
            session.recv_counter += 1;
            mk
        };

        // Decrypt with AES-GCM
        if payload.ciphertext.len() < AES_GCM_TAG_SIZE {
            return Err("Ciphertext too short".into());
        }

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&message_key));
        let nonce = Nonce::from_slice(&payload.nonce);

        let plaintext = cipher
            .decrypt(nonce, payload.ciphertext.as_ref())
            .map_err(|e| format!("Decryption failed: {}", e))?;

        // Zero the message key
        let mut key_copy = message_key;
        for byte in &mut key_copy {
            *byte = 0;
        }

        Ok(plaintext)
    }

    /// Performs X3DH key agreement using our identity key against remote keys
    fn perform_x3dh(&self, remote_signed_pre_key: &[u8], remote_pre_key: &[u8]) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
        // Import local identity key as ephemeral secret for ECDH
        let mut local_secret_bytes = [0u8; 32];
        local_secret_bytes.copy_from_slice(&self.identity_private_key);
        let local_secret = StaticSecret::from(local_secret_bytes);

        // DH with signed pre-key
        let mut remote_signed_bytes = [0u8; 32];
        remote_signed_bytes.copy_from_slice(remote_signed_pre_key);
        let remote_signed_public = X25519PublicKey::from(remote_signed_bytes);
        let dh1 = local_secret.diffie_hellman(&remote_signed_public);

        // DH with one-time pre-key
        let mut remote_pre_bytes = [0u8; 32];
        remote_pre_bytes.copy_from_slice(remote_pre_key);
        let remote_pre_public = X25519PublicKey::from(remote_pre_bytes);
        let dh2 = local_secret.diffie_hellman(&remote_pre_public);

        // Concatenate DH results
        let mut shared_secret = Vec::new();
        shared_secret.extend_from_slice(dh1.as_bytes());
        shared_secret.extend_from_slice(dh2.as_bytes());

        Ok(shared_secret)
    }

    /// Advances a chain key by one step, returning new chain key and message key
    fn ratchet_chain_key(&self, chain_key: &[u8], info: &[u8]) -> Result<(Vec<u8>, Vec<u8>), Box<dyn std::error::Error>> {
        let hk = Hkdf::<Sha256>::new(Some(&[0x01u8]), chain_key);

        let mut message_key = vec![0u8; AES_KEY_SIZE];
        hk.expand(info, &mut message_key)?;

        let hk2 = Hkdf::<Sha256>::new(Some(&[0x02u8]), chain_key);
        let mut new_chain_key = vec![0u8; AES_KEY_SIZE];
        hk2.expand(info, &mut new_chain_key)?;

        Ok((new_chain_key, message_key))
    }

    /// Returns a copy of the Ed25519 public key
    pub fn get_public_key(&self) -> Vec<u8> {
        self.identity_public_key.clone()
    }
}

impl Default for SignalProtocolService {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_service_creation() {
        let service = SignalProtocolService::new();
        assert_eq!(service.get_public_key().len(), 32);
    }

    #[test]
    fn test_pre_key_bundle_generation() {
        let service = SignalProtocolService::new();
        let bundle = service.generate_pre_key_bundle("test-node").unwrap();

        assert_eq!(bundle.uhid, "test-node");
        assert_eq!(bundle.identity_key.len(), 32);
        assert_eq!(bundle.pre_key.len(), 32);
        assert_eq!(bundle.signed_pre_key.len(), 32);
        assert_eq!(bundle.signed_pre_key_signature.len(), 64);
    }

    #[test]
    fn test_session_establishment_and_encryption() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();

        // Bob generates pre-key bundle
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();

        // Alice processes Bob's bundle and establishes session
        alice.process_pre_key_bundle(&bob_bundle).unwrap();
        assert!(alice.has_session("bob"));

        // Alice encrypts a message
        let plaintext = b"Hello, Bob!";
        let encrypted = alice.encrypt("bob", plaintext).unwrap();

        // Bob must establish session with Alice first
        let alice_bundle = alice.generate_pre_key_bundle("alice").unwrap();
        bob.process_pre_key_bundle(&alice_bundle).unwrap();

        // Bob decrypts the message
        let decrypted = bob.decrypt("alice", &encrypted).unwrap();
        assert_eq!(decrypted, plaintext);
    }
}

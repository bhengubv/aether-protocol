// SPDX-License-Identifier: MIT

//! Signal Protocol implementation: X3DH + Double-Ratchet.
//!
//! Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
//!   * DH1 = DH(IK_A, SPK_B) — long-term mutual authentication
//!   * DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
//!   * DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
//!   * DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
//!
//! Root-key derivation: HKDF-SHA256 over `concat(DH1||DH2||DH3||DH4)`.
//! Symmetric ratchet: HMAC-SHA256, single-byte domain separation
//!   (0x01 -> message key, 0x02 -> next chain key) per Signal §5.1.
//! Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
//! Identity signing: Ed25519.

use crate::constants::{AES_GCM_NONCE_SIZE, AES_GCM_TAG_SIZE, AES_KEY_SIZE};
use crate::models::{EncryptedPayload, PreKeyBundle, SignalSession};
use aes_gcm::{aead::Aead, Aes256Gcm, Key, KeyInit, Nonce};
use hkdf::Hkdf;
use hmac::{Hmac, Mac};
use rand::Rng;
use sha2::Sha256;
use std::collections::HashMap;
use x25519_dalek::{PublicKey as X25519PublicKey, StaticSecret};

type HmacSha256 = Hmac<Sha256>;

pub const MAX_SKIPPED_KEYS: usize = 1000;

pub const MESSAGE_TYPE_NORMAL: i32 = 0;
pub const MESSAGE_TYPE_PRE_KEY: i32 = 1;

const X25519_PUBLIC_KEY_SIZE: usize = 32;

/// HKDF info strings — these MUST match the C# reference exactly. Any drift
/// breaks cross-language interop (verified by
/// `fixtures/signal/expected/x3dh_basic.json`).
const HKDF_X3DH_ROOT_INFO: &[u8] = b"aether-x3dh-root-v1";
const HKDF_CHAIN_INITIATOR_SEND_INFO: &[u8] = b"aether-chain-initiator-send-v1";
const HKDF_CHAIN_INITIATOR_RECV_INFO: &[u8] = b"aether-chain-initiator-recv-v1";

/// Responder-side pre-key state. Holds the private halves of the signed
/// pre-key and one-time pre-keys so X3DH can be computed when an initiator's
/// PreKey message arrives.
#[derive(Default)]
struct PreKeyState {
    signed_pre_key_id: i32,
    signed_pre_key_priv: Vec<u8>,
    signed_pre_key_pub: Vec<u8>,
    signed_pre_key_signature: Vec<u8>,
    /// id -> (priv, pub). Each entry consumed (zeroed and removed) on first use.
    one_time_pre_keys: HashMap<i32, (Vec<u8>, Vec<u8>)>,
}

pub struct SignalProtocolService {
    // Long-term identity keys — two distinct keypairs per node.
    identity_x25519_priv: [u8; 32],
    identity_x25519_pub: [u8; 32],
    ed25519_private_key: Vec<u8>,
    ed25519_public_key: Vec<u8>,

    /// Local UHID — captured when generate_pre_key_bundle is called or via
    /// set_local_uhid. Stamped on outbound EncryptedPayloads.
    local_uhid: Option<String>,

    /// Pre-key state held for responder-side X3DH.
    pre_keys: PreKeyState,

    sessions: HashMap<String, SignalSession>,
}

impl SignalProtocolService {
    pub fn new() -> Self {
        let (ed_priv, ed_pub) = crate::security::Ed25519SigningService::generate_keypair();

        // X25519 long-term identity for X3DH ECDH.
        let mut rng = rand::thread_rng();
        let x_priv_secret = StaticSecret::random_from_rng(&mut rng);
        let x_priv: [u8; 32] = x_priv_secret.to_bytes();
        let x_pub: [u8; 32] = X25519PublicKey::from(&x_priv_secret).to_bytes();

        SignalProtocolService {
            identity_x25519_priv: x_priv,
            identity_x25519_pub: x_pub,
            ed25519_private_key: ed_priv,
            ed25519_public_key: ed_pub,
            local_uhid: None,
            pre_keys: PreKeyState::default(),
            sessions: HashMap::new(),
        }
    }

    /// Sets the local node's UHID. Required before any encrypt() call.
    pub fn set_local_uhid(&mut self, uhid: &str) {
        self.local_uhid = Some(uhid.to_string());
    }

    pub fn has_session(&self, peer_uhid: &str) -> bool {
        self.sessions.contains_key(peer_uhid)
    }

    pub fn get_public_key(&self) -> Vec<u8> {
        self.ed25519_public_key.clone()
    }

    pub fn get_x25519_public_key(&self) -> Vec<u8> {
        self.identity_x25519_pub.to_vec()
    }

    /// Generates a pre-key bundle. Retains the SPK + OPK private halves for
    /// responder-side X3DH on this node.
    pub fn generate_pre_key_bundle(
        &mut self,
        local_uhid: &str,
    ) -> Result<PreKeyBundle, Box<dyn std::error::Error>> {
        self.local_uhid = Some(local_uhid.to_string());
        let mut rng = rand::thread_rng();

        // One-time pre-key.
        let otpk_secret = StaticSecret::random_from_rng(&mut rng);
        let otpk_priv: [u8; 32] = otpk_secret.to_bytes();
        let otpk_pub: [u8; 32] = X25519PublicKey::from(&otpk_secret).to_bytes();
        let pre_key_id = rng.gen_range(1..i32::MAX);
        self.pre_keys
            .one_time_pre_keys
            .insert(pre_key_id, (otpk_priv.to_vec(), otpk_pub.to_vec()));

        // Signed pre-key.
        let spk_secret = StaticSecret::random_from_rng(&mut rng);
        let spk_priv: [u8; 32] = spk_secret.to_bytes();
        let spk_pub: [u8; 32] = X25519PublicKey::from(&spk_secret).to_bytes();
        let signed_pre_key_id = rng.gen_range(1..i32::MAX);
        let signature =
            crate::security::Ed25519SigningService::sign(&self.ed25519_private_key, &spk_pub)?;
        self.pre_keys.signed_pre_key_id = signed_pre_key_id;
        self.pre_keys.signed_pre_key_priv = spk_priv.to_vec();
        self.pre_keys.signed_pre_key_pub = spk_pub.to_vec();
        self.pre_keys.signed_pre_key_signature = signature.clone();

        Ok(PreKeyBundle::new(
            local_uhid.to_string(),
            self.ed25519_public_key.clone(),
            self.identity_x25519_pub.to_vec(),
            pre_key_id,
            otpk_pub.to_vec(),
            signed_pre_key_id,
            spk_pub.to_vec(),
            signature,
        ))
    }

    /// Establishes initiator-side session via X3DH (Signal §3.3): generates
    /// a fresh ephemeral X25519 keypair, runs the four DHs, derives the
    /// root key, and primes the symmetric ratchet.
    pub fn process_pre_key_bundle(
        &mut self,
        bundle: &PreKeyBundle,
    ) -> Result<(), Box<dyn std::error::Error>> {
        if !crate::security::Ed25519SigningService::verify(
            &bundle.identity_key,
            &bundle.signed_pre_key,
            &bundle.signed_pre_key_signature,
        ) {
            return Err("Signed pre-key signature verification failed".into());
        }
        if bundle.identity_key_x25519.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!(
                "Bundle has malformed X25519 identity key (length {})",
                bundle.identity_key_x25519.len()
            )
            .into());
        }
        if bundle.signed_pre_key.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!(
                "Bundle has malformed signed pre-key (length {})",
                bundle.signed_pre_key.len()
            )
            .into());
        }
        if bundle.pre_key.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!(
                "Bundle has malformed one-time pre-key (length {})",
                bundle.pre_key.len()
            )
            .into());
        }

        // Fresh ephemeral X25519 keypair, generated per-session.
        let mut rng = rand::thread_rng();
        let ek_secret = StaticSecret::random_from_rng(&mut rng);
        let ek_priv: [u8; 32] = ek_secret.to_bytes();
        let ek_pub: [u8; 32] = X25519PublicKey::from(&ek_secret).to_bytes();

        // X3DH 4-DH key agreement (initiator side).
        let dh1 = x25519_agree(&self.identity_x25519_priv, &bundle.signed_pre_key)?;
        let dh2 = x25519_agree(&ek_priv, &bundle.identity_key_x25519)?;
        let dh3 = x25519_agree(&ek_priv, &bundle.signed_pre_key)?;
        let dh4 = x25519_agree(&ek_priv, &bundle.pre_key)?;

        let mut shared = Vec::with_capacity(128);
        shared.extend_from_slice(&dh1);
        shared.extend_from_slice(&dh2);
        shared.extend_from_slice(&dh3);
        shared.extend_from_slice(&dh4);

        let root_key = hkdf32(&shared, HKDF_X3DH_ROOT_INFO)?;
        let send_chain_key = hkdf32(&root_key, HKDF_CHAIN_INITIATOR_SEND_INFO)?;
        let recv_chain_key = hkdf32(&root_key, HKDF_CHAIN_INITIATOR_RECV_INFO)?;

        let mut session = SignalSession::new(bundle.uhid.clone(), bundle.identity_key.clone());
        session.root_key = root_key;
        session.send_chain_key = send_chain_key;
        session.recv_chain_key = recv_chain_key;
        session.pending_pre_key_message = true;
        session.initiator_identity_key_x25519 = self.identity_x25519_pub.to_vec();
        session.initiator_ephemeral_key_x25519 = ek_pub.to_vec();
        session.used_signed_pre_key_id = bundle.signed_pre_key_id;
        session.used_one_time_pre_key_id = bundle.pre_key_id;

        self.sessions.insert(bundle.uhid.clone(), session);

        // Best-effort scrubbing.
        zero(&mut shared);
        Ok(())
    }

    pub fn encrypt(
        &mut self,
        peer_uhid: &str,
        plaintext: &[u8],
    ) -> Result<EncryptedPayload, Box<dyn std::error::Error>> {
        let sender = self
            .local_uhid
            .clone()
            .ok_or("Local UHID is not set. Call generate_pre_key_bundle or set_local_uhid first.")?;

        let session = self
            .sessions
            .get_mut(peer_uhid)
            .ok_or("No session established with peer")?;

        let (new_chain, message_key) = ratchet_chain_key(&session.send_chain_key);
        session.send_chain_key = new_chain;

        let mut rng = rand::thread_rng();
        let mut nonce_bytes = [0u8; AES_GCM_NONCE_SIZE];
        rng.fill(&mut nonce_bytes);

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&message_key));
        let nonce = Nonce::from_slice(&nonce_bytes);
        let ciphertext = cipher
            .encrypt(nonce, plaintext)
            .map_err(|e| format!("Encryption failed: {}", e))?;

        let counter = session.send_counter;
        session.send_counter += 1;

        let mut payload = EncryptedPayload::new(
            ciphertext,
            nonce_bytes.to_vec(),
            MESSAGE_TYPE_NORMAL,
            sender,
            counter,
        );

        if session.pending_pre_key_message {
            payload.message_type = MESSAGE_TYPE_PRE_KEY;
            payload.initiator_identity_key_x25519 =
                Some(session.initiator_identity_key_x25519.clone());
            payload.initiator_ephemeral_key_x25519 =
                Some(session.initiator_ephemeral_key_x25519.clone());
            payload.used_signed_pre_key_id = session.used_signed_pre_key_id;
            payload.used_one_time_pre_key_id = session.used_one_time_pre_key_id;
            session.pending_pre_key_message = false;
        }

        let mut mk_copy = message_key;
        zero(&mut mk_copy);
        Ok(payload)
    }

    pub fn decrypt(
        &mut self,
        peer_uhid: &str,
        payload: &EncryptedPayload,
    ) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
        if payload.message_type == MESSAGE_TYPE_PRE_KEY {
            let ik = payload
                .initiator_identity_key_x25519
                .as_ref()
                .ok_or("PreKey message missing initiator identity key")?;
            let ek = payload
                .initiator_ephemeral_key_x25519
                .as_ref()
                .ok_or("PreKey message missing initiator ephemeral key")?;
            self.establish_responder_session(
                peer_uhid,
                ik,
                ek,
                payload.used_signed_pre_key_id,
                payload.used_one_time_pre_key_id,
            )?;
        }

        let session = self
            .sessions
            .get_mut(peer_uhid)
            .ok_or("No session established with peer")?;

        if payload.ciphertext.len() < AES_GCM_TAG_SIZE {
            return Err("Ciphertext too short".into());
        }

        let message_key = if let Some(skipped) = session.skipped_message_keys.remove(&payload.counter) {
            skipped
        } else {
            let gap = payload.counter as i64 - session.recv_counter as i64;
            if gap > MAX_SKIPPED_KEYS as i64 {
                return Err(format!(
                    "Message counter gap ({}) exceeds maximum ({}). Session must be re-established.",
                    gap, MAX_SKIPPED_KEYS
                )
                .into());
            }
            while session.recv_counter < payload.counter {
                let (nc, sk) = ratchet_chain_key(&session.recv_chain_key);
                session.recv_chain_key = nc;
                session.skipped_message_keys.insert(session.recv_counter, sk);
                session.recv_counter += 1;
            }
            let (nc, mk) = ratchet_chain_key(&session.recv_chain_key);
            session.recv_chain_key = nc;
            session.recv_counter += 1;
            mk
        };

        let cipher = Aes256Gcm::new(Key::<Aes256Gcm>::from_slice(&message_key));
        let nonce = Nonce::from_slice(&payload.nonce);
        let plaintext = cipher
            .decrypt(nonce, payload.ciphertext.as_ref())
            .map_err(|e| format!("Decryption failed: {}", e))?;

        let mut mk_copy = message_key;
        zero(&mut mk_copy);
        Ok(plaintext)
    }

    /// Mirrors the initiator's 4 X3DH DHs to derive the same root key, then
    /// derives chain keys with send/recv roles SWAPPED relative to the
    /// initiator. Consumes (and zeros) the one-time pre-key.
    fn establish_responder_session(
        &mut self,
        peer_uhid: &str,
        initiator_ik: &[u8],
        initiator_ek: &[u8],
        used_signed_pre_key_id: i32,
        used_one_time_pre_key_id: i32,
    ) -> Result<(), Box<dyn std::error::Error>> {
        if initiator_ik.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!("Initiator IK_X25519 wrong size: {}", initiator_ik.len()).into());
        }
        if initiator_ek.len() != X25519_PUBLIC_KEY_SIZE {
            return Err(format!("Initiator EK_X25519 wrong size: {}", initiator_ek.len()).into());
        }
        if self.pre_keys.signed_pre_key_id != used_signed_pre_key_id
            || self.pre_keys.signed_pre_key_priv.is_empty()
        {
            return Err(format!(
                "PreKey message references signed pre-key id {} which is not held",
                used_signed_pre_key_id
            )
            .into());
        }
        let otpk = self
            .pre_keys
            .one_time_pre_keys
            .remove(&used_one_time_pre_key_id)
            .ok_or_else(|| {
                format!(
                    "PreKey message references one-time pre-key id {} which is not held (already consumed?)",
                    used_one_time_pre_key_id
                )
            })?;

        // Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
        let dh1 = x25519_agree(&self.pre_keys.signed_pre_key_priv, initiator_ik)?;
        let dh2 = x25519_agree(&self.identity_x25519_priv, initiator_ek)?;
        let dh3 = x25519_agree(&self.pre_keys.signed_pre_key_priv, initiator_ek)?;
        let dh4 = x25519_agree(&otpk.0, initiator_ek)?;

        let mut shared = Vec::with_capacity(128);
        shared.extend_from_slice(&dh1);
        shared.extend_from_slice(&dh2);
        shared.extend_from_slice(&dh3);
        shared.extend_from_slice(&dh4);

        let root_key = hkdf32(&shared, HKDF_X3DH_ROOT_INFO)?;
        // SWAPPED: initiator's send-chain info derives our recv-chain.
        let recv_chain_key = hkdf32(&root_key, HKDF_CHAIN_INITIATOR_SEND_INFO)?;
        let send_chain_key = hkdf32(&root_key, HKDF_CHAIN_INITIATOR_RECV_INFO)?;

        let mut session = SignalSession::new(peer_uhid.to_string(), Vec::new());
        session.root_key = root_key;
        session.send_chain_key = send_chain_key;
        session.recv_chain_key = recv_chain_key;
        self.sessions.insert(peer_uhid.to_string(), session);

        // Consume one-time pre-key — already removed by .remove() above; zero its priv copy.
        let mut otpk_copy = otpk.0;
        zero(&mut otpk_copy);
        zero(&mut shared);
        Ok(())
    }
}

impl Default for SignalProtocolService {
    fn default() -> Self {
        Self::new()
    }
}

/// X25519 ECDH. Returns 32 raw shared-secret bytes.
///
/// RFC 7748 §6.1: detect the all-zero output (small-subgroup attack via a
/// low-order remote public key).
fn x25519_agree(local_priv: &[u8], remote_pub: &[u8]) -> Result<[u8; 32], Box<dyn std::error::Error>> {
    if local_priv.len() != X25519_PUBLIC_KEY_SIZE {
        return Err(format!("X25519 private key must be 32 bytes, got {}", local_priv.len()).into());
    }
    if remote_pub.len() != X25519_PUBLIC_KEY_SIZE {
        return Err(format!("X25519 public key must be 32 bytes, got {}", remote_pub.len()).into());
    }
    let mut priv_arr = [0u8; 32];
    priv_arr.copy_from_slice(local_priv);
    let mut pub_arr = [0u8; 32];
    pub_arr.copy_from_slice(remote_pub);

    let secret = StaticSecret::from(priv_arr);
    let pub_key = X25519PublicKey::from(pub_arr);
    let shared = secret.diffie_hellman(&pub_key);
    let bytes = shared.to_bytes();

    let mut acc: u8 = 0;
    for &b in &bytes {
        acc |= b;
    }
    if acc == 0 {
        return Err("X25519 produced an all-zero shared secret (low-order point)".into());
    }
    Ok(bytes)
}

/// HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# HKDF.DeriveKey.
fn hkdf32(ikm: &[u8], info: &[u8]) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let hk = Hkdf::<Sha256>::new(None, ikm);
    let mut out = vec![0u8; AES_KEY_SIZE];
    hk.expand(info, &mut out)
        .map_err(|e| format!("HKDF expand failed: {}", e))?;
    Ok(out)
}

/// Single Double-Ratchet step (Signal §5.1).
fn ratchet_chain_key(chain_key: &[u8]) -> (Vec<u8>, Vec<u8>) {
    let mut mac1 =
        HmacSha256::new_from_slice(chain_key).expect("HMAC keys are arbitrary-length");
    mac1.update(&[0x01]);
    let message_key = mac1.finalize().into_bytes().to_vec();

    let mut mac2 =
        HmacSha256::new_from_slice(chain_key).expect("HMAC keys are arbitrary-length");
    mac2.update(&[0x02]);
    let new_chain = mac2.finalize().into_bytes().to_vec();
    (new_chain, message_key)
}

fn zero(buf: &mut [u8]) {
    for b in buf.iter_mut() {
        *b = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_service_creation() {
        let service = SignalProtocolService::new();
        assert_eq!(service.get_public_key().len(), 32);
        assert_eq!(service.get_x25519_public_key().len(), 32);
    }

    #[test]
    fn test_pre_key_bundle_has_both_identity_keys() {
        let mut service = SignalProtocolService::new();
        let bundle = service.generate_pre_key_bundle("alice").unwrap();
        assert_eq!(bundle.identity_key.len(), 32);          // Ed25519
        assert_eq!(bundle.identity_key_x25519.len(), 32);   // X25519
        assert_ne!(bundle.identity_key, bundle.identity_key_x25519);
        assert_eq!(bundle.pre_key.len(), 32);
        assert_eq!(bundle.signed_pre_key.len(), 32);
        assert_eq!(bundle.signed_pre_key_signature.len(), 64);
    }

    #[test]
    fn test_x3dh_first_message_round_trips() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();

        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let encrypted = alice.encrypt("bob", b"the mesh is alive").unwrap();
        assert_eq!(encrypted.message_type, MESSAGE_TYPE_PRE_KEY);
        assert_eq!(encrypted.sender_uhid, "alice");
        assert!(encrypted.initiator_identity_key_x25519.is_some());
        assert!(encrypted.initiator_ephemeral_key_x25519.is_some());

        let plaintext = bob.decrypt("alice", &encrypted).unwrap();
        assert_eq!(plaintext, b"the mesh is alive");
        assert!(bob.has_session("alice"));
    }

    #[test]
    fn test_subsequent_message_is_normal() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let first = alice.encrypt("bob", b"a").unwrap();
        bob.decrypt("alice", &first).unwrap();

        let second = alice.encrypt("bob", b"b").unwrap();
        assert_eq!(second.message_type, MESSAGE_TYPE_NORMAL);
        assert!(second.initiator_identity_key_x25519.is_none());

        let out = bob.decrypt("alice", &second).unwrap();
        assert_eq!(out, b"b");
    }

    #[test]
    fn test_bidirectional_after_first_message() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let a = alice.encrypt("bob", b"ping").unwrap();
        assert_eq!(bob.decrypt("alice", &a).unwrap(), b"ping");

        let b = bob.encrypt("alice", b"pong").unwrap();
        assert_eq!(b.message_type, MESSAGE_TYPE_NORMAL);
        assert_eq!(alice.decrypt("bob", &b).unwrap(), b"pong");
    }

    #[test]
    fn test_five_sequential_messages_ratchet_forward() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        for i in 0..5u8 {
            let enc = alice.encrypt("bob", &[i]).unwrap();
            assert_eq!(enc.counter, i as u32);
            let dec = bob.decrypt("alice", &enc).unwrap();
            assert_eq!(dec, [i]);
        }
    }

    #[test]
    fn test_one_time_pre_key_consumed() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        alice.generate_pre_key_bundle("alice").unwrap();
        alice.process_pre_key_bundle(&bob_bundle).unwrap();

        let first = alice.encrypt("bob", b"first").unwrap();
        bob.decrypt("alice", &first).unwrap();

        // Replay using the same bundle should fail.
        let mut alice2 = SignalProtocolService::new();
        alice2.generate_pre_key_bundle("alice2").unwrap();
        alice2.process_pre_key_bundle(&bob_bundle).unwrap();
        let replay = alice2.encrypt("bob", b"replay").unwrap();
        assert!(bob.decrypt("alice2", &replay).is_err());
    }

    #[test]
    fn test_encrypt_without_local_uhid_errors() {
        let mut alice = SignalProtocolService::new();
        let mut bob = SignalProtocolService::new();
        let bob_bundle = bob.generate_pre_key_bundle("bob").unwrap();
        // Note: no generate_pre_key_bundle / set_local_uhid on Alice.
        alice.process_pre_key_bundle(&bob_bundle).unwrap();
        assert!(alice.encrypt("bob", b"x").is_err());
    }
}

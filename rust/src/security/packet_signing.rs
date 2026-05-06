// SPDX-License-Identifier: MIT

use crate::constants::*;
use crate::protocol::MeshPacket;
use std::collections::{HashMap, VecDeque};
use std::time::{SystemTime, UNIX_EPOCH};

/// Service for packet signing and nonce deduplication
pub struct PacketSigningService {
    /// Map of (sender_uhid, nonce) -> timestamp for replay detection
    seen_nonces: HashMap<String, VecDeque<(Vec<u8>, u64)>>,
}

impl PacketSigningService {
    pub fn new() -> Self {
        PacketSigningService {
            seen_nonces: HashMap::new(),
        }
    }

    /// Signs a packet with a fresh nonce and timestamp
    pub fn sign_packet(
        &self,
        packet: &mut MeshPacket,
        private_key: &[u8],
    ) -> Result<(), Box<dyn std::error::Error>> {
        // Fill nonce with random 8 bytes
        let mut nonce = [0u8; PACKET_NONCE_SIZE];
        use rand::RngCore;
        rand::thread_rng().fill_bytes(&mut nonce);
        packet.packet_nonce = nonce.to_vec();

        // Set timestamp to current time in milliseconds
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;
        packet.timestamp_ms = now;

        // Construct signable data
        let signable_data = packet.signable_data();

        // Sign with Ed25519
        let signature = crate::security::Ed25519SigningService::sign(private_key, &signable_data)?;
        packet.signature = signature;

        Ok(())
    }

    /// Verifies a packet's signature and freshness
    pub fn verify_packet(
        &mut self,
        packet: &MeshPacket,
        public_key: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        // Check freshness (5 minute window)
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;

        let age_ms = now - packet.timestamp_ms;
        if age_ms > (MAX_PACKET_AGE_SECONDS as i64 * 1000) {
            return Ok(false); // Packet too old
        }

        if age_ms < 0 {
            return Ok(false); // Packet from future (clock skew)
        }

        // Dedup is keyed by (source_uhid, nonce). The outer HashMap is keyed
        // by source_uhid and the per-source VecDeque holds raw nonce bytes —
        // structurally equivalent to keying by (source, nonce) but cheaper
        // (no per-lookup string allocation) and the per-source partition lets
        // us age-prune nonces from a single peer without scanning the world.
        // Crucially: two different peers MAY legitimately use the same random
        // nonce — they MUST NOT collide here.
        let nonce_entry = &packet.packet_nonce;
        let timestamp = now as u64;

        let nonce_history = self
            .seen_nonces
            .entry(packet.source_uhid.clone())
            .or_insert_with(VecDeque::new);

        // Remove expired entries (older than 5 minutes)
        while let Some((_, ts)) = nonce_history.front() {
            if (timestamp - ts) > MAX_PACKET_AGE_SECONDS {
                nonce_history.pop_front();
            } else {
                break;
            }
        }

        // Check if this nonce has been seen
        for (seen_nonce, _) in nonce_history.iter() {
            if seen_nonce == nonce_entry {
                return Ok(false); // Duplicate nonce
            }
        }

        // Record this nonce
        nonce_history.push_back((nonce_entry.clone(), timestamp));

        // Verify signature
        let signable_data = packet.signable_data();
        let is_valid = crate::security::Ed25519SigningService::verify(public_key, &signable_data, &packet.signature);

        Ok(is_valid)
    }

    /// Cleans up old nonce entries
    pub fn cleanup(&mut self) {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();

        for history in self.seen_nonces.values_mut() {
            while let Some((_, ts)) = history.front() {
                if (now - ts) > MAX_PACKET_AGE_SECONDS {
                    history.pop_front();
                } else {
                    break;
                }
            }
        }

        // Remove empty histories
        self.seen_nonces.retain(|_, v| !v.is_empty());
    }
}

impl Default for PacketSigningService {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::PacketType;

    #[test]
    fn test_sign_and_verify_packet() {
        let (private_key, public_key) = crate::security::Ed25519SigningService::generate_keypair();
        let mut signer = PacketSigningService::new();
        let mut verifier = PacketSigningService::new();

        let mut packet = MeshPacket::new(PacketType::Data, "node-a".to_string());
        packet.destination_uhid = "node-b".to_string();
        packet.payload = b"test payload".to_vec();

        // Sign the packet
        signer.sign_packet(&mut packet, &private_key).unwrap();
        assert!(!packet.signature.is_empty());
        assert!(!packet.packet_nonce.is_empty());

        // Verify the packet
        let is_valid = verifier.verify_packet(&packet, &public_key).unwrap();
        assert!(is_valid);
    }

    /// Dedup MUST be keyed by (source_uhid, nonce) — two different peers
    /// happening to randomly pick the same 8 nonce bytes must NOT collide.
    /// Otherwise an attacker who observes peer A's nonce can lock peer B out
    /// by replaying a forged packet with peer B's source UHID.
    #[test]
    fn test_dedup_keyed_by_source_and_nonce() {
        let (priv_a, pub_a) = crate::security::Ed25519SigningService::generate_keypair();
        let (priv_b, pub_b) = crate::security::Ed25519SigningService::generate_keypair();
        let signer = PacketSigningService::new();
        let mut verifier = PacketSigningService::new();

        // Two packets from two different sources, with deliberately-equal nonces.
        let mut a = MeshPacket::new(PacketType::Data, "node-a".to_string());
        a.payload = b"alpha".to_vec();
        signer.sign_packet(&mut a, &priv_a).unwrap();

        let mut b = MeshPacket::new(PacketType::Data, "node-b".to_string());
        b.payload = b"bravo".to_vec();
        signer.sign_packet(&mut b, &priv_b).unwrap();

        // Force a collision on the nonce bytes — but each is signed under
        // its own source-keyed signature.
        b.packet_nonce = a.packet_nonce.clone();
        // Re-sign B because mutating the nonce invalidates the signature.
        b.signature = crate::security::Ed25519SigningService::sign(
            &priv_b,
            &b.signable_data(),
        )
        .unwrap();

        assert!(
            verifier.verify_packet(&a, &pub_a).unwrap(),
            "first sender's nonce verifies"
        );
        assert!(
            verifier.verify_packet(&b, &pub_b).unwrap(),
            "second sender with same nonce bytes MUST also verify — dedup is keyed by (source, nonce)"
        );

        // But replaying A's exact packet again MUST fail (same source, same nonce).
        assert!(
            !verifier.verify_packet(&a, &pub_a).unwrap(),
            "exact replay (same source, same nonce) must be rejected"
        );
    }

    #[test]
    fn test_reject_duplicate_nonce() {
        let (private_key, public_key) = crate::security::Ed25519SigningService::generate_keypair();
        let mut signer = PacketSigningService::new();
        let mut verifier = PacketSigningService::new();

        let mut packet = MeshPacket::new(PacketType::Data, "node-a".to_string());
        packet.payload = b"test".to_vec();

        // Sign with specific nonce
        signer.sign_packet(&mut packet, &private_key).unwrap();
        let original_nonce = packet.packet_nonce.clone();

        // First verification should succeed
        let is_valid = verifier.verify_packet(&packet, &public_key).unwrap();
        assert!(is_valid);

        // Same nonce again should be rejected
        packet.packet_nonce = original_nonce.clone();
        let is_valid = verifier.verify_packet(&packet, &public_key).unwrap();
        assert!(!is_valid);
    }
}

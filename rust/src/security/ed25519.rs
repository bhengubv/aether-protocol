// SPDX-License-Identifier: MIT

use ed25519_dalek::{Keypair, PublicKey, SecretKey, Signature, SigningKey, VerifyingKey};
use rand::Rng;

/// Ed25519 signing service for Aether protocol
pub struct Ed25519SigningService;

impl Ed25519SigningService {
    /// Generates a new Ed25519 key pair
    /// Returns (private_key: 32 bytes, public_key: 32 bytes)
    pub fn generate_keypair() -> (Vec<u8>, Vec<u8>) {
        let mut rng = rand::thread_rng();
        let mut seed = [0u8; 32];
        rng.fill(&mut seed);

        let signing_key = SigningKey::from_bytes(&seed);
        let verifying_key = signing_key.verifying_key();

        (seed.to_vec(), verifying_key.to_bytes().to_vec())
    }

    /// Signs data using an Ed25519 private key
    /// private_key must be 32 bytes (seed)
    /// Returns 64-byte signature
    pub fn sign(private_key: &[u8], data: &[u8]) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
        if private_key.len() != 32 {
            return Err("Ed25519 private key must be 32 bytes".into());
        }

        let signing_key = SigningKey::from_bytes(private_key);
        let signature = signing_key.sign(data);

        Ok(signature.to_bytes().to_vec())
    }

    /// Verifies an Ed25519 signature
    /// public_key must be 32 bytes
    /// signature must be 64 bytes
    pub fn verify(public_key: &[u8], data: &[u8], signature: &[u8]) -> bool {
        if public_key.len() != 32 {
            return false;
        }

        if signature.len() != 64 {
            return false;
        }

        let Ok(pk_bytes) = <[u8; 32]>::try_from(public_key) else {
            return false;
        };

        let Ok(sig_bytes) = <[u8; 64]>::try_from(signature) else {
            return false;
        };

        let Ok(verifying_key) = VerifyingKey::from_bytes(&pk_bytes) else {
            return false;
        };

        let Ok(signature_obj) = Signature::from_bytes(&sig_bytes) else {
            return false;
        };

        verifying_key.verify_strict(data, &signature_obj).is_ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_generate_keypair() {
        let (private_key, public_key) = Ed25519SigningService::generate_keypair();
        assert_eq!(private_key.len(), 32);
        assert_eq!(public_key.len(), 32);
    }

    #[test]
    fn test_sign_and_verify() {
        let (private_key, public_key) = Ed25519SigningService::generate_keypair();
        let data = b"test message";

        let signature = Ed25519SigningService::sign(&private_key, data).unwrap();
        assert_eq!(signature.len(), 64);

        assert!(Ed25519SigningService::verify(&public_key, data, &signature));
    }

    #[test]
    fn test_verify_invalid_signature() {
        let (_, public_key) = Ed25519SigningService::generate_keypair();
        let data = b"test message";
        let invalid_sig = vec![0u8; 64];

        assert!(!Ed25519SigningService::verify(
            &public_key,
            data,
            &invalid_sig
        ));
    }

    #[test]
    fn test_verify_tampered_data() {
        let (private_key, public_key) = Ed25519SigningService::generate_keypair();
        let data = b"test message";
        let tampered = b"tampered message";

        let signature = Ed25519SigningService::sign(&private_key, data).unwrap();
        assert!(!Ed25519SigningService::verify(
            &public_key,
            tampered,
            &signature
        ));
    }
}

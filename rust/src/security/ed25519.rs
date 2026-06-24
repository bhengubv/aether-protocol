// SPDX-License-Identifier: MIT

use ed25519_dalek::{Signature, Signer, SigningKey, VerifyingKey};
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

        let pk_arr = <[u8; 32]>::try_from(private_key)
            .map_err(|_| "Ed25519 private key must be 32 bytes")?;
        let signing_key = SigningKey::from_bytes(&pk_arr);
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

        let signature_obj = Signature::from_bytes(&sig_bytes);

        verifying_key.verify_strict(data, &signature_obj).is_ok()
    }

    /// Verifies a signature, trying Ed25519 first and falling back to legacy P-256
    /// ECDSA for public keys longer than 32 bytes (Protocol Version 1 identity keys
    /// during the migration window — see PROTOCOL_SPEC.md §7.5).
    ///
    /// A 32-byte key takes the Ed25519 path; a longer key is a DER SubjectPublicKeyInfo
    /// P-256 key verified against an ASN.1 DER ECDSA signature over SHA-256.
    pub fn verify_with_fallback(public_key: &[u8], data: &[u8], signature: &[u8]) -> bool {
        if public_key.len() == 32 {
            Self::verify(public_key, data, signature)
        } else {
            Self::verify_p256(public_key, data, signature)
        }
    }

    /// Verifies a legacy P-256 (secp256r1) ECDSA signature over SHA-256.
    /// Public key is X.509 SubjectPublicKeyInfo (DER); signature is ASN.1 DER.
    fn verify_p256(spki_public_key: &[u8], data: &[u8], der_signature: &[u8]) -> bool {
        use p256::ecdsa::signature::Verifier;
        use p256::ecdsa::{Signature, VerifyingKey};
        use p256::pkcs8::DecodePublicKey;

        let Ok(verifying_key) = VerifyingKey::from_public_key_der(spki_public_key) else {
            return false;
        };
        let Ok(sig) = Signature::from_der(der_signature) else {
            return false;
        };
        verifying_key.verify(data, &sig).is_ok()
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

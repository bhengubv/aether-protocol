// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Ed25519 signing and verification service using CryptoKit.
/// Key format: 32-byte private key (Curve25519), 32-byte public key, 64-byte signature.
public struct Ed25519Service {
    /// Generates a new Ed25519 key pair.
    /// - Returns: A tuple of (privateKey: 32-byte seed, publicKey: 32-byte point)
    public static func generateKeyPair() -> (privateKey: Data, publicKey: Data) {
        let privateKey = Curve25519.Signing.PrivateKey()
        let privateKeyBytes = Data(privateKey.rawRepresentation)
        let publicKeyBytes = Data(privateKey.publicKey.rawRepresentation)
        return (privateKeyBytes, publicKeyBytes)
    }

    /// Signs data using an Ed25519 private key.
    /// - Parameters:
    ///   - privateKey: 32-byte Ed25519 private key seed
    ///   - data: The data to sign
    /// - Returns: 64-byte Ed25519 signature
    /// - Throws: Ed25519Error if the private key is invalid
    public static func sign(_ privateKey: Data, _ data: Data) throws -> Data {
        guard privateKey.count == 32 else {
            throw Ed25519Error.invalidKeySize("Private key must be 32 bytes, got \(privateKey.count)")
        }

        let key = try Curve25519.Signing.PrivateKey(rawRepresentation: privateKey)
        let signature = try key.signature(for: data)
        return Data(signature)
    }

    /// Verifies an Ed25519 signature.
    /// - Parameters:
    ///   - publicKey: 32-byte Ed25519 public key
    ///   - data: The signed data
    ///   - signature: 64-byte Ed25519 signature
    /// - Returns: True if the signature is valid
    public static func verify(_ publicKey: Data, _ data: Data, _ signature: Data) -> Bool {
        guard publicKey.count == 32 else { return false }
        guard signature.count == 64 else { return false }

        do {
            let key = try Curve25519.Signing.PublicKey(rawRepresentation: publicKey)
            return key.isValidSignature(signature, for: data)
        } catch {
            return false
        }
    }

    /// Verifies a signature, trying Ed25519 first and falling back to legacy P-256
    /// ECDSA for public keys longer than 32 bytes (Protocol Version 1 identity keys
    /// during the migration window — see PROTOCOL_SPEC.md §7.5).
    ///
    /// A 32-byte key takes the Ed25519 path; a longer key is a DER SubjectPublicKeyInfo
    /// P-256 key verified against an ASN.1 DER ECDSA signature over SHA-256.
    public static func verifyWithFallback(_ publicKey: Data, _ data: Data, _ signature: Data) -> Bool {
        if publicKey.count == 32 {
            return verify(publicKey, data, signature)
        }
        return verifyP256(publicKey, data, signature)
    }

    /// Verifies a legacy P-256 (secp256r1) ECDSA signature over SHA-256.
    /// Public key is X.509 SubjectPublicKeyInfo (DER); signature is ASN.1 DER.
    private static func verifyP256(_ spkiPublicKey: Data, _ data: Data, _ derSignature: Data) -> Bool {
        do {
            let key = try P256.Signing.PublicKey(derRepresentation: spkiPublicKey)
            let sig = try P256.Signing.ECDSASignature(derRepresentation: derSignature)
            return key.isValidSignature(sig, for: data)
        } catch {
            return false
        }
    }
}

public enum Ed25519Error: Error {
    case invalidKeySize(String)
    case invalidSignature(String)
}

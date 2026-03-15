// SPDX-License-Identifier: MIT

namespace Aether.Security.Models;

/// <summary>
/// Represents an asymmetric key pair.
/// </summary>
/// <param name="PublicKey">The public key bytes.</param>
/// <param name="PrivateKey">The private key bytes.</param>
public sealed record KeyPair(byte[] PublicKey, byte[] PrivateKey);

/// <summary>
/// An encrypted payload with all metadata needed for decryption.
/// </summary>
/// <param name="Ciphertext">The encrypted data.</param>
/// <param name="Nonce">The AES-GCM nonce used for encryption.</param>
/// <param name="MessageType">The type of message (0 = normal, 1 = pre-key message).</param>
/// <param name="SenderUhid">The sender's Universal Hash ID.</param>
/// <param name="Counter">The message counter within the current sending chain.</param>
public sealed record EncryptedPayload(
    byte[] Ciphertext,
    byte[] Nonce,
    int MessageType,
    string SenderUhid,
    int Counter);

/// <summary>
/// A pre-key bundle published by a node for X3DH key agreement.
/// </summary>
/// <param name="Uhid">The node's Universal Hash ID.</param>
/// <param name="IdentityKey">The node's long-term identity public key.</param>
/// <param name="PreKeyId">The one-time pre-key identifier.</param>
/// <param name="PreKey">The one-time pre-key public key.</param>
/// <param name="SignedPreKeyId">The signed pre-key identifier.</param>
/// <param name="SignedPreKey">The signed pre-key public key.</param>
/// <param name="SignedPreKeySignature">Ed25519 signature over the signed pre-key.</param>
public sealed record PreKeyBundle(
    string Uhid,
    byte[] IdentityKey,
    int PreKeyId,
    byte[] PreKey,
    int SignedPreKeyId,
    byte[] SignedPreKey,
    byte[] SignedPreKeySignature);

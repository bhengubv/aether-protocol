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
///
/// When <see cref="MessageType"/> is 1 (PreKey message — the first message
/// from an initiator before a session is established on the responder side),
/// <see cref="InitiatorIdentityKeyX25519"/> and
/// <see cref="InitiatorEphemeralKeyX25519"/> carry the data the responder
/// needs to run X3DH on its side and derive the same root key. On normal
/// session messages those fields are null.
/// </summary>
/// <param name="Ciphertext">The encrypted data.</param>
/// <param name="Nonce">The AES-GCM nonce used for encryption.</param>
/// <param name="MessageType">0 = normal session message, 1 = PreKey (initial) message.</param>
/// <param name="SenderUhid">The sender's Universal Hash ID.</param>
/// <param name="Counter">The message counter within the current sending chain.</param>
/// <param name="InitiatorIdentityKeyX25519">PreKey messages: the initiator's
///   long-term X25519 identity public key (32 bytes). Null on normal messages.</param>
/// <param name="InitiatorEphemeralKeyX25519">PreKey messages: the initiator's
///   ephemeral X25519 public key (32 bytes), generated fresh per session. Null
///   on normal messages.</param>
/// <param name="UsedSignedPreKeyId">PreKey messages: the SignedPreKeyId from the
///   recipient's bundle that the initiator consumed. 0 on normal messages.</param>
/// <param name="UsedOneTimePreKeyId">PreKey messages: the one-time PreKeyId from
///   the recipient's bundle that the initiator consumed. 0 on normal messages.</param>
public sealed record EncryptedPayload(
    byte[] Ciphertext,
    byte[] Nonce,
    int MessageType,
    string SenderUhid,
    int Counter,
    byte[]? InitiatorIdentityKeyX25519 = null,
    byte[]? InitiatorEphemeralKeyX25519 = null,
    int UsedSignedPreKeyId = 0,
    int UsedOneTimePreKeyId = 0);

/// <summary>
/// A pre-key bundle published by a node for X3DH key agreement.
///
/// Two identity keys per node: <see cref="IdentityKey"/> is the long-term
/// Ed25519 signing key; <see cref="IdentityKeyX25519"/> is the long-term
/// X25519 ECDH key used in the X3DH DH operations. Separate keypairs is the
/// simpler alternative to XEdDSA (Signal's scheme for using one Curve25519
/// key for both signing and ECDH).
/// </summary>
/// <param name="Uhid">The node's Universal Hash ID.</param>
/// <param name="IdentityKey">The node's long-term Ed25519 identity public key (32 bytes).</param>
/// <param name="IdentityKeyX25519">The node's long-term X25519 ECDH public key (32 bytes).</param>
/// <param name="PreKeyId">The one-time pre-key identifier.</param>
/// <param name="PreKey">The one-time pre-key X25519 public key (32 bytes).</param>
/// <param name="SignedPreKeyId">The signed pre-key identifier.</param>
/// <param name="SignedPreKey">The signed pre-key X25519 public key (32 bytes).</param>
/// <param name="SignedPreKeySignature">Ed25519 signature (by <see cref="IdentityKey"/>) over <see cref="SignedPreKey"/>.</param>
public sealed record PreKeyBundle(
    string Uhid,
    byte[] IdentityKey,
    byte[] IdentityKeyX25519,
    int PreKeyId,
    byte[] PreKey,
    int SignedPreKeyId,
    byte[] SignedPreKey,
    byte[] SignedPreKeySignature);

// SPDX-License-Identifier: MIT

namespace AetherMesh.Security.Models;

/// <summary>
/// Represents an asymmetric key pair.
/// </summary>
/// <param name="PublicKey">The public key bytes.</param>
/// <param name="PrivateKey">The private key bytes.</param>
public sealed record KeyPair(byte[] PublicKey, byte[] PrivateKey);

/// <summary>
/// An encrypted payload with all metadata needed for decryption.
///
/// Two layered ratchets contribute fields:
///
/// 1. **X3DH session-establishment** (Signal §3) — populated only on the
///    first message a new initiator sends to a peer (MessageType=1):
///    <see cref="InitiatorIdentityKeyX25519"/>, <see cref="UsedSignedPreKeyId"/>,
///    <see cref="UsedOneTimePreKeyId"/>. The responder uses these to run
///    X3DH on its side and derive the same root key.
///
/// 2. **Double Ratchet** (Signal §5) — <see cref="SenderEphemeralKeyX25519"/>
///    and <see cref="PreviousChainCount"/> populated on EVERY message.
///    SenderEphemeralKeyX25519 is the sender's current DH-ratchet public key;
///    when it changes between messages, the receiver runs a DH-ratchet step
///    that re-keys the chain and gives per-roundtrip forward secrecy and
///    post-compromise security. On the very first PreKey message, this
///    equals the X3DH ephemeral public key (Signal-canonical integration:
///    initiator's X3DH ephemeral becomes its first DH-ratchet public).
/// </summary>
/// <param name="Ciphertext">The encrypted data.</param>
/// <param name="Nonce">The AES-GCM nonce used for encryption.</param>
/// <param name="MessageType">0 = normal session message, 1 = PreKey (initial) message.</param>
/// <param name="SenderUhid">The sender's Universal Hash ID.</param>
/// <param name="Counter">The message counter within the current sending chain (Signal §5: Ns).</param>
/// <param name="InitiatorIdentityKeyX25519">PreKey messages: the initiator's
///   long-term X25519 identity public key (32 bytes). Null on normal messages.</param>
/// <param name="InitiatorEphemeralKeyX25519">DEPRECATED: use
///   <see cref="SenderEphemeralKeyX25519"/> instead. Kept for backward
///   compatibility with consumers of the pre-Double-Ratchet wire envelope.
///   On PreKey messages this equals SenderEphemeralKeyX25519; on normal
///   messages it is null. New consumers should ignore this field.</param>
/// <param name="UsedSignedPreKeyId">PreKey messages: the SignedPreKeyId from the
///   recipient's bundle that the initiator consumed. 0 on normal messages.</param>
/// <param name="UsedOneTimePreKeyId">PreKey messages: the one-time PreKeyId from
///   the recipient's bundle that the initiator consumed. 0 on normal messages.</param>
/// <param name="SenderEphemeralKeyX25519">The sender's current DH-ratchet
///   X25519 public key (32 bytes). Populated on every message. Drives the
///   DH-ratchet step on the receiver side: when this value changes, the
///   receiver re-keys the chain via KDF_RK(rootKey, DH(myDHs, newDHr)).</param>
/// <param name="PreviousChainCount">Number of messages the sender sent in
///   its previous sending chain (Signal §5: PN). Used by the receiver to
///   compute skipped message keys when crossing a DH-ratchet boundary.</param>
public sealed record EncryptedPayload(
    byte[] Ciphertext,
    byte[] Nonce,
    int MessageType,
    string SenderUhid,
    int Counter,
    byte[]? InitiatorIdentityKeyX25519 = null,
    byte[]? InitiatorEphemeralKeyX25519 = null,
    int UsedSignedPreKeyId = 0,
    int UsedOneTimePreKeyId = 0,
    byte[]? SenderEphemeralKeyX25519 = null,
    int PreviousChainCount = 0);

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

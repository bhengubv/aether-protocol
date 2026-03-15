// SPDX-License-Identifier: MIT

using Aether.Protocol;
using Aether.Security.Models;

namespace Aether.Security.Services;

/// <summary>
/// Signal Protocol service for end-to-end encrypted messaging.
/// Provides X3DH key agreement, Double Ratchet symmetric ratcheting,
/// and AES-GCM authenticated encryption.
/// </summary>
public interface ISignalProtocolService
{
    /// <summary>
    /// Returns true if an active session exists with the specified peer.
    /// </summary>
    bool HasSession(string peerUhid);

    /// <summary>
    /// Encrypts plaintext for a peer using the current session's sending chain.
    /// </summary>
    Task<EncryptedPayload> EncryptAsync(string peerUhid, byte[] plaintext, CancellationToken ct = default);

    /// <summary>
    /// Decrypts an encrypted payload from a peer using the session's receiving chain.
    /// </summary>
    Task<byte[]> DecryptAsync(string peerUhid, EncryptedPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Generates a pre-key bundle for this node that can be published to
    /// allow other nodes to initiate sessions.
    /// </summary>
    Task<PreKeyBundle> GeneratePreKeyBundleAsync(string localUhid, CancellationToken ct = default);

    /// <summary>
    /// Processes a received pre-key bundle to establish an outbound session
    /// with the bundle's owner via X3DH.
    /// </summary>
    Task ProcessPreKeyBundleAsync(PreKeyBundle bundle, CancellationToken ct = default);

    /// <summary>
    /// Signs data using the local identity key (Ed25519).
    /// </summary>
    Task<byte[]> SignDataAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Verifies an Ed25519 signature against a public key.
    /// </summary>
    bool VerifySignature(byte[] publicKey, byte[] data, byte[] signature);
}

/// <summary>
/// Signs and verifies MeshPacket signatures for authentication and replay protection.
/// </summary>
public interface IPacketSigningService
{
    /// <summary>
    /// Signs a MeshPacket by filling in PacketNonce, TimestampMs, ProtocolVersion,
    /// and computing the Ed25519 signature.
    /// </summary>
    Task<MeshPacket> SignPacketAsync(MeshPacket packet, CancellationToken ct = default);

    /// <summary>
    /// Verifies a MeshPacket's signature, timestamp freshness, and nonce uniqueness.
    /// </summary>
    Task<bool> VerifyPacketAsync(MeshPacket packet, byte[] senderPublicKey, CancellationToken ct = default);
}

/// <summary>
/// Persistent storage for cryptographic keys.
/// </summary>
public interface IKeyStorageService
{
    /// <summary>
    /// Returns the existing identity key pair, or generates and stores a new one.
    /// </summary>
    Task<KeyPair> GetOrCreateIdentityKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores a key under the given identifier.
    /// </summary>
    Task StoreKeyAsync(string keyId, byte[] keyData, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a previously stored key by identifier, or null if not found.
    /// </summary>
    Task<byte[]?> RetrieveKeyAsync(string keyId, CancellationToken ct = default);
}

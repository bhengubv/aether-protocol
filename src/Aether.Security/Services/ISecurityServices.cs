// SPDX-License-Identifier: MIT

using AetherMesh.Protocol;
using AetherMesh.Security.Models;

namespace AetherMesh.Security.Services;

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

/// <summary>
/// Persistent storage for Signal Protocol session state. Each session is keyed
/// by the peer's UHID. Implementations are responsible for atomicity and
/// durability — the protocol layer hands an opaque <see cref="SignalSession"/>
/// in and trusts that <see cref="LoadAsync"/> later returns the exact same
/// state (or null if no session was previously stored).
///
/// The interface is internal because <see cref="SignalSession"/> is internal
/// — exposed via <c>InternalsVisibleTo</c> to <c>Aether.Storage</c> (for the
/// KV-backed adapter) and <c>Aether.Core.Tests</c> (for verification).
/// </summary>
internal interface ISignalSessionStore
{
    Task<SignalSession?> LoadAsync(string peerUhid, CancellationToken ct = default);
    Task SaveAsync(string peerUhid, SignalSession session, CancellationToken ct = default);
    Task DeleteAsync(string peerUhid, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken ct = default);
}

/// <summary>
/// DTO carrying the long-term identity key material that survives across
/// process restarts. The Ed25519 keypair signs pre-key bundles; the X25519
/// keypair participates in X3DH agreement. Both private halves stay on the
/// node and are never transmitted.
///
/// <see cref="LocalUhid"/> is persisted alongside the keys so that
/// <c>EncryptAsync</c> still works after a restart without the host having
/// to call <c>SetLocalUhid</c> again.
/// </summary>
public sealed record StoredIdentityKeys(
    byte[] Ed25519PrivateKey,
    byte[] Ed25519PublicKey,
    byte[] X25519PrivateKey,
    byte[] X25519PublicKey,
    string? LocalUhid = null);

/// <summary>
/// One signed pre-key entry as stored in the SPK history. Each rotation
/// generates a new entry; the active entry is the most-recently-generated
/// one. Older entries are retained for the configured rotation window so
/// that messages signed under a recently-rotated SPK can still decrypt.
/// </summary>
public sealed record StoredSignedPreKey(
    int Id,
    byte[] PrivateKey,
    byte[] PublicKey,
    byte[] Signature,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Full signed-pre-key history: the active SPK plus the retained prior
/// entries in generation order (oldest first). Empty until first
/// <c>GeneratePreKeyBundleAsync</c> call.
/// </summary>
public sealed record StoredSignedPreKeyHistory(
    IReadOnlyList<StoredSignedPreKey> Entries);

/// <summary>
/// One one-time pre-key in the pool. Removed from the store on consumption
/// (Signal §3.3 — each OPK is consumed exactly once).
/// </summary>
public sealed record StoredOneTimePreKey(
    int Id,
    byte[] PrivateKey,
    byte[] PublicKey,
    bool Issued);

/// <summary>
/// Persistent storage for the long-term identity keys, signed-pre-key
/// history, and one-time pre-key pool. All methods are best-effort from the
/// caller's perspective: failures are logged but never propagate up the
/// message-flow stack.
///
/// Implementations are not required to be thread-safe; <see cref="SignalProtocolService"/>
/// serialises access through its own pre-key lock before calling.
/// </summary>
public interface IPreKeyStore
{
    Task<StoredIdentityKeys?> LoadIdentityAsync(CancellationToken ct = default);
    Task SaveIdentityAsync(StoredIdentityKeys identity, CancellationToken ct = default);
    Task<StoredSignedPreKeyHistory> LoadSignedPreKeysAsync(CancellationToken ct = default);
    Task SaveSignedPreKeysAsync(StoredSignedPreKeyHistory history, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, StoredOneTimePreKey>> LoadOneTimePreKeysAsync(CancellationToken ct = default);
    Task SaveOneTimePreKeysAsync(IReadOnlyDictionary<int, StoredOneTimePreKey> pool, CancellationToken ct = default);
    Task ConsumeOneTimePreKeyAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Configuration for periodic signed-pre-key rotation (Signal §3.3 — keys
/// SHOULD be rotated periodically).
///
/// On every <c>GeneratePreKeyBundleAsync</c> call the service checks whether
/// the active SPK is older than <see cref="RotationInterval"/>; if it is,
/// a fresh SPK is generated and the old one is appended to the history.
/// The history is then trimmed to keep at most
/// <see cref="RetainedHistoryCount"/> prior entries (plus the new active
/// one). Messages signed under any retained SPK still decrypt; messages
/// signed under a pruned SPK fail.
/// </summary>
public sealed record SignedPreKeyRotationOptions(
    TimeSpan RotationInterval,
    int RetainedHistoryCount)
{
    public static SignedPreKeyRotationOptions Default { get; } =
        new(TimeSpan.FromDays(7), RetainedHistoryCount: 3);
}

// SPDX-License-Identifier: MIT
namespace Aether.Vault.Models;

/// <summary>
/// Manifest for a file stored in the Aether Vault.
///
/// The manifest is the only thing the owner needs to retain — it contains all
/// metadata required to reconstruct the file from any <see cref="K"/> of the
/// <see cref="ShardHashes"/>.
/// </summary>
public sealed class VaultManifest
{
    /// <summary>Random file identifier — the only external reference to this vault entry.</summary>
    public Guid FileId { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 hash of the reassembled plaintext — used for integrity verification on recovery.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>AES-256-GCM encryption salt for key derivation from the owner's Ed25519 private key.</summary>
    public byte[] EncryptionSalt { get; set; } = [];

    /// <summary>
    /// IContentService hashes for each shard's ciphertext.
    /// Length == <see cref="K"/> + <see cref="M"/> (default 14).
    /// </summary>
    public string[] ShardHashes { get; set; } = [];

    /// <summary>Minimum number of shards required for reconstruction (default 10).</summary>
    public int K { get; set; } = 10;

    /// <summary>Number of redundancy shards beyond K (default 4).</summary>
    public int M { get; set; } = 4;

    /// <summary>UTC timestamp when the file was first vaulted.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Original file size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>User-visible label (stored encrypted in a production implementation).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Total shards = K + M.</summary>
    public int TotalShards => K + M;
}

// SPDX-License-Identifier: MIT
namespace Aether.Vault.Models;

/// <summary>One encrypted shard of a vaulted file.</summary>
public sealed class VaultShard
{
    /// <summary>IContentService hash of this shard's ciphertext bytes.</summary>
    public string ShardHash { get; set; } = string.Empty;

    /// <summary>Zero-based index within the shard set (0 to K+M−1).</summary>
    public int ShardIndex { get; set; }

    /// <summary>Owning file identifier, XOR-encrypted with a per-shard key (opaque to the host node).</summary>
    public byte[] EncFileId { get; set; } = [];

    /// <summary>The raw shard bytes (ciphertext). Present only when the shard is held locally.</summary>
    public byte[]? Data { get; set; }
}

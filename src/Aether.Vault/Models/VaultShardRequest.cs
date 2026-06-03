// SPDX-License-Identifier: MIT
namespace Aether.Vault.Models;

/// <summary>Event args fired when a peer requests a shard that this node holds.</summary>
public sealed class VaultShardRequest
{
    /// <summary>The shard hash being requested.</summary>
    public string ShardHash { get; set; } = string.Empty;

    /// <summary>UHID of the requesting peer.</summary>
    public string RequesterUhid { get; set; } = string.Empty;
}

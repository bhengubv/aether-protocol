// SPDX-License-Identifier: MIT

namespace AetherNet.Streaming.Models;

/// <summary>
/// One file entry within a multi-file torrent.
/// </summary>
public sealed class TorrentFile
{
    /// <summary>Relative path inside the torrent (may be a multi-segment path for multi-file torrents).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// Everything a mesh peer needs to join the BitTorrent swarm for a watch-together session.
/// Carried in a <see cref="AetherNet.Protocol.PacketType.TorrentMetadata"/> broadcast packet.
/// </summary>
public sealed class TorrentInfo
{
    /// <summary>Hex-encoded SHA-1 info-hash of the torrent.</summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>Magnet URI — preferred for peers that don't want to store the full .torrent file.</summary>
    public string? MagnetLink { get; set; }

    /// <summary>Raw .torrent file bytes — optional, provided when the host has the full file.</summary>
    public byte[]? TorrentFileData { get; set; }

    /// <summary>Display name (from the torrent's "name" field).</summary>
    public string? Name { get; set; }

    /// <summary>Total download size in bytes (sum of all files).</summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>Number of pieces in the torrent.</summary>
    public int PieceCount { get; set; }

    /// <summary>Size of each piece in bytes (typically 256 KB – 4 MB).</summary>
    public int PieceSizeBytes { get; set; }

    /// <summary>File listing for multi-file torrents. Single-file torrents have one entry.</summary>
    public List<TorrentFile> Files { get; set; } = new();
}

/// <summary>
/// Lifecycle state of a ChipIn pool.
/// </summary>
public enum ChipInState : byte
{
    /// <summary>Pool is open and accepting contributions.</summary>
    Collecting = 0,
    /// <summary>Target amount reached; ready to purchase.</summary>
    Funded = 1,
    /// <summary>Purchase in progress (server-side).</summary>
    Purchasing = 2,
    /// <summary>Content purchased and available.</summary>
    Acquired = 3,
    /// <summary>Purchase failed after funding.</summary>
    Failed = 4,
    /// <summary>Contributions returned to wallets.</summary>
    Refunded = 5,
}

/// <summary>
/// A single contribution to a <see cref="ChipInPool"/>.
/// </summary>
public sealed class ChipInContribution
{
    /// <summary>UHID of the contributing participant.</summary>
    public string ContributorUhid { get; set; } = string.Empty;

    /// <summary>Contribution amount in South African Rand (ZAR).</summary>
    public decimal AmountZar { get; set; }

    /// <summary>SDPKT ledger transaction ID — nullable when contributed offline.</summary>
    public Guid? SdpktTransactionId { get; set; }

    /// <summary>UTC timestamp of the contribution.</summary>
    public DateTimeOffset ContributedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Group fund pool for content acquisition within a watch-together session.
/// Created by the host and updated as participants contribute.
/// </summary>
public sealed class ChipInPool
{
    /// <summary>Pool identifier (local and broadcast).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The watch session this pool belongs to.</summary>
    public Guid SessionId { get; set; }

    /// <summary>UHID of the participant who created the pool.</summary>
    public string InitiatorUhid { get; set; } = string.Empty;

    /// <summary>Target amount to collect before transitioning to <see cref="ChipInState.Funded"/> (ZAR).</summary>
    public decimal TargetAmountZar { get; set; }

    /// <summary>Total amount collected so far (ZAR).</summary>
    public decimal CollectedAmountZar { get; set; }

    /// <summary>Current pool lifecycle state.</summary>
    public ChipInState State { get; set; } = ChipInState.Collecting;

    /// <summary>Human-readable content description shown to potential contributors.</summary>
    public string? ContentDescription { get; set; }

    /// <summary>Hex info-hash of the torrent to acquire (when using BitTorrent ingest).</summary>
    public string? TorrentInfoHash { get; set; }

    /// <summary>Magnet URI for the content to acquire.</summary>
    public string? MagnetLink { get; set; }

    /// <summary>Contributions received so far.</summary>
    public List<ChipInContribution> Contributions { get; set; } = new();

    /// <summary>UTC timestamp the pool was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when <see cref="CollectedAmountZar"/> has reached or exceeded <see cref="TargetAmountZar"/>.</summary>
    public bool IsFunded => CollectedAmountZar >= TargetAmountZar;
}

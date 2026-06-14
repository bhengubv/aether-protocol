// SPDX-License-Identifier: MIT

namespace AetherNet.Tipping.Models;

/// <summary>
/// The kind of relayed mesh traffic a tip is paying for. Each value maps to a
/// distinct <see cref="TipPolicy"/> (min/max/daily-cap/suggested) so a host can
/// price message relay differently from, say, a live-stream relay or DTN custody.
/// </summary>
public enum TipTrafficType
{
    MessageRelay,
    ChunkServe,
    StreamRelay,
    DtnCustody,
    DtnDelivery,
    VoiceRelay,
    GatewayShare,
    Direct
}

/// <summary>
/// Quality-of-service preference tier earned by consistent tippers. Higher tiers
/// add a QoS boost to a node's routing quality score — a preference, never an
/// access gate. Non-tippers always get service at <see cref="Standard"/>.
/// </summary>
public enum QoSTier
{
    Standard = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3
}

/// <summary>
/// Server-driven regulated policy for a single <see cref="TipTrafficType"/>:
/// the allowed amount band, the per-tipper daily cap, and the suggested
/// auto-tip amount. Cached locally so tipping works offline.
/// </summary>
public class TipPolicy
{
    public TipTrafficType TrafficType { get; set; }
    public decimal MinAmount { get; set; }
    public decimal MaxAmount { get; set; }
    public decimal DailyCapPerTipper { get; set; }
    public decimal SuggestedAmount { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// A node operator's local registration profile: the SDPKT wallet tips settle into,
/// whether the operator currently accepts tips, and rolling earning/relay stats.
/// </summary>
public class NodeOperatorProfile
{
    public string Uhid { get; set; } = string.Empty;
    public string SdpktWalletAddress { get; set; } = string.Empty;
    public bool IsRegistered { get; set; }
    public bool AcceptsTips { get; set; } = true;
    public decimal TotalEarned { get; set; }
    public long TotalRelays { get; set; }
    public double UptimePercentage { get; set; }
    public DateTimeOffset OperatorSince { get; set; }
}

/// <summary>
/// A tip queued on-device, awaiting batch sync to the backend (which settles it
/// into both wallets via the ledger). Persisted so a tip survives an app restart
/// and an offline period.
/// </summary>
public class LocalTipTransaction
{
    public long Id { get; set; }
    public string TipperUhid { get; set; } = string.Empty;
    public string RecipientUhid { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public TipTrafficType TrafficType { get; set; }
    public Guid? ReferenceId { get; set; }
    public bool IsSynced { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A tipper's earned standing: total tipped, count, a 0-100 consistency score,
/// and the <see cref="QoSTier"/> that score maps to.
/// </summary>
public class TipperReputation
{
    public string TipperUhid { get; set; } = string.Empty;
    public decimal TotalTipped { get; set; }
    public int TipCount { get; set; }
    public short ConsistencyScore { get; set; }
    public QoSTier Tier { get; set; }
    public DateTimeOffset? LastTippedAt { get; set; }
}

/// <summary>
/// A node operator's reputation as scored by the backend: reliability, relay
/// volume, uptime, 30-day tip earnings, a composite score, and abuse flags.
/// Bad actors (selective relaying, snooping) can be flagged and delisted.
/// </summary>
public class NodeReputation
{
    public string NodeUhid { get; set; } = string.Empty;
    public short ReliabilityScore { get; set; }
    public long RelayVolume { get; set; }
    public double UptimePercent { get; set; }
    public decimal TipEarnings30d { get; set; }
    public short CompositeScore { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsDelisted { get; set; }
}

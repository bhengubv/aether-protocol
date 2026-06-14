// SPDX-License-Identifier: MIT

namespace AetherNet.Tipping;

/// <summary>
/// Default tip policy values and QoS thresholds for the SDPKT-settlement tipping
/// layer. These are host/value-level constants (ZAR amounts, XP, QoS bands) — they
/// live here, alongside the tipping layer they configure, rather than in the
/// value-agnostic protocol-wide <see cref="AetherNet.Constants.ProtocolConstants"/>.
///
/// <para>
/// The amounts are regulated defaults used when the backend has not pushed a
/// server-driven <see cref="Models.TipPolicy"/>: a tip is allowed only within the
/// min/max band, and a single tipper is capped per day. Tipping is never an access
/// gate — non-tippers always get service; consistent tippers merely earn a QoS
/// preference boost.
/// </para>
/// </summary>
public static class TipPolicyConstants
{
    // ── Tipping — PacketType ────────────────────────────────────────────────────
    /// <summary>
    /// Wire value of <see cref="AetherNet.Protocol.PacketType.TipPacket"/> (24).
    /// Kept here as the canonical tipping-layer reference to the protocol value.
    /// </summary>
    public const int TipPacketType = 24;

    // ── Tip caps (ZAR via SDPKT) ────────────────────────────────────────────────
    /// <summary>Smallest tip a host will accept by default (ZAR).</summary>
    public const decimal DefaultTipMinZar = 0.10m;

    /// <summary>Largest single tip a host will accept by default (ZAR).</summary>
    public const decimal DefaultTipMaxZar = 50.00m;

    /// <summary>Default per-tipper rolling daily cap (ZAR).</summary>
    public const decimal DefaultDailyCapZar = 100.00m;

    /// <summary>How many queued tips are pushed per backend batch-sync call.</summary>
    public const int TipSyncBatchSize = 50;

    // ── XP reward for a mesh tip ────────────────────────────────────────────────
    /// <summary>XP credited to the tipper for sending a mesh tip.</summary>
    public const int XpMeshTip = 5;

    // ── Suggested tips per traffic type (ZAR) ───────────────────────────────────
    public const decimal SuggestedTipMessageRelay = 0.10m;
    public const decimal SuggestedTipChunkServe = 0.50m;
    public const decimal SuggestedTipStreamRelay = 1.00m;
    public const decimal SuggestedTipDtnCustody = 0.25m;
    public const decimal SuggestedTipVoiceRelay = 0.50m;

    // ── QoS tipper tiers (consistency score thresholds 0-100) ───────────────────
    public const short QoSBronzeThreshold = 25;
    public const short QoSSilverThreshold = 50;
    public const short QoSGoldThreshold = 75;

    // ── QoS boost added to a route's quality score ──────────────────────────────
    public const short QoSBoostBronze = 5;
    public const short QoSBoostSilver = 10;
    public const short QoSBoostGold = 20;
}

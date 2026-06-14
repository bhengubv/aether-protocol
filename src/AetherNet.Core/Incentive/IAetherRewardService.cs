// SPDX-License-Identifier: MIT

namespace AetherNet.Incentive;

/// <summary>
/// Queues XP reward events locally and batch-syncs them (50 per call) to the
/// backend/incentives service. The on-device queue means a node earns XP for relay
/// work performed while offline and reconciles it the next time it has connectivity.
/// </summary>
public interface IAetherRewardService
{
    /// <summary>Queue an XP reward for a mesh action (relay, gateway-share, mesh-tip, …).</summary>
    Task QueueRewardAsync(string actionType, int xpAmount, string? description = null, Guid? referenceId = null);

    /// <summary>Number of rewards queued locally and not yet synced.</summary>
    Task<int> GetPendingCountAsync();

    /// <summary>
    /// Batch-sync queued rewards to the backend (50 per call). Returns the total
    /// number the server accepted. On any failure the loop stops and the remaining
    /// rewards stay queued for the next cycle — never throws.
    /// </summary>
    Task<int> SyncToServerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical action-type strings for mesh reward events. These are the wire
/// <c>action_type</c> values the backend reward ledger keys XP on.
/// </summary>
public static class AetherRewardActions
{
    public const string RelayPacket = "relay_packet";
    public const string GatewayShare = "gateway_share";
    public const string ContentSeed = "content_seed";
    public const string SosRelay = "sos_relay";
    public const string MeshTip = "mesh_tip";
    public const string ChannelCreate = "channel_create";
    public const string RouteMaintain = "route_maintain";
    public const string StreamRelay = "stream_relay";
    public const string StreamBroadcast = "stream_broadcast";
    public const string VoiceRelay = "voice_relay";
    public const string DtnCustody = "dtn_custody";
    public const string DtnDelivery = "dtn_delivery";
    public const string PresenceRelay = "presence_relay";

    // Phase 7: Video
    public const string VideoRelay = "video_relay";
    public const string WatchTogetherHost = "watch_together_host";
    public const string TorrentBridge = "torrent_bridge";
}

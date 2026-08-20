// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Streaming.Models;

namespace AetherNet.Streaming;


/// <summary>
/// Synchronized playback across a small group. The host issues authoritative
/// <see cref="WatchSyncCommand"/>s (Play / Pause / Seek / Speed); followers apply
/// them locally with RTT compensation so playback stays aligned. Reactions are
/// follower-to-everyone fire-and-forget.
///
/// This service does NOT move bytes — it expects the underlying media to already
/// be available to every participant via <c>AetherNet.Content</c> (or the host) and
/// just keeps timecodes aligned.
/// </summary>
public interface IWatchTogetherService
{
    /// <summary>Raised when a host announces a watch-together session this node should join.</summary>
    event EventHandler<WatchSession>? SessionInvited;

    /// <summary>Raised when a sync command updates this follower's local session state.</summary>
    event EventHandler<WatchSession>? SyncApplied;

    /// <summary>Raised when a reaction arrives from any participant.</summary>
    event EventHandler<WatchReactionPayload>? ReactionReceived;

    /// <summary>Raised when the session ends (host left or explicit end).</summary>
    event EventHandler<WatchSession>? SessionEnded;

    /// <summary>Host-side: start a watch-together session for the given content. Broadcasts a join announce so peers can discover it.</summary>
    Task<WatchSession> HostAsync(string contentRootHash, string title, WatchMode mode = WatchMode.SharedFile, CancellationToken cancellationToken = default);

    /// <summary>Follower-side: explicitly join an announced session.</summary>
    Task FollowAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Host-side: emit a <see cref="WatchSyncType.Play"/> command at the given position.</summary>
    Task PlayAsync(Guid sessionId, long positionMs, CancellationToken cancellationToken = default);

    /// <summary>Host-side: emit a <see cref="WatchSyncType.Pause"/> command at the given position.</summary>
    Task PauseAsync(Guid sessionId, long positionMs, CancellationToken cancellationToken = default);

    /// <summary>Host-side: emit a <see cref="WatchSyncType.Seek"/> command.</summary>
    Task SeekAsync(Guid sessionId, long positionMs, CancellationToken cancellationToken = default);

    /// <summary>Host-side: change playback speed.</summary>
    Task SetSpeedAsync(Guid sessionId, double playbackSpeed, long positionMs, CancellationToken cancellationToken = default);

    /// <summary>Any participant: send a reaction.</summary>
    Task SendReactionAsync(Guid sessionId, string reaction, long positionMs, CancellationToken cancellationToken = default);

    /// <summary>End the session (host only).</summary>
    Task EndAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Pump an inbound watch-together packet.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Sessions this node currently hosts or follows.</summary>
    IReadOnlyList<WatchSession> GetActiveSessions();

    // ─── BitTorrent ingest ──────────────────────────────────────────────────

    /// <summary>Raised when a <see cref="PacketType.TorrentMetadata"/> packet arrives in a session this node participates in.</summary>
    event EventHandler<(Guid SessionId, TorrentInfo Torrent)>? TorrentReceived;

    /// <summary>Raised whenever a <see cref="ChipInPool"/> is created or its state changes.</summary>
    event EventHandler<ChipInPool>? ChipInUpdated;

    /// <summary>Host-side: broadcast <see cref="PacketType.TorrentMetadata"/> to all session participants.</summary>
    Task BroadcastTorrentAsync(Guid sessionId, TorrentInfo torrent, CancellationToken ct = default);

    // ─── ChipIn ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a ChipIn pool for content acquisition in a session and broadcast it to all participants.
    /// </summary>
    Task<ChipInPool> StartChipInAsync(Guid sessionId, decimal targetAmountZar, string? contentDescription, string? torrentInfoHash, string? magnetLink, CancellationToken ct = default);

    /// <summary>
    /// Add a contribution to an existing pool.
    /// Transitions to <see cref="ChipInState.Funded"/> when <c>CollectedAmountZar &gt;= TargetAmountZar</c>.
    /// Returns the updated pool, or <see langword="null"/> if the pool was not found.
    /// </summary>
    Task<ChipInPool?> ContributeAsync(Guid chipInId, string contributorUhid, decimal amountZar, CancellationToken ct = default);

    /// <summary>Returns the pool with the given id, or <see langword="null"/> if unknown.</summary>
    ChipInPool? GetChipIn(Guid chipInId);
}

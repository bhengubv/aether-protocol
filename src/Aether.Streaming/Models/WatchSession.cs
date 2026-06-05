// SPDX-License-Identifier: MIT

namespace AetherMesh.Streaming.Models;

/// <summary>
/// Lifecycle state of a watch-together session.
/// </summary>
public enum WatchState : byte
{
    Idle = 0,
    Hosting = 1,
    Following = 2,
    Ended = 3,
}

/// <summary>
/// State of a single watch-together session. The host controls playback; followers
/// apply <see cref="WatchSyncCommand"/>s with RTT compensation. The reference content
/// being played is identified by <see cref="ContentRootHash"/> (a hash from
/// <c>Aether.Content</c>) — the watch layer doesn't move bytes itself, it just
/// synchronizes timecodes across already-distributed content.
/// </summary>
public sealed class WatchSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the host (the only node allowed to issue authoritative sync commands).</summary>
    public string HostUhid { get; set; } = string.Empty;

    /// <summary>This node's role.</summary>
    public WatchState State { get; set; } = WatchState.Idle;

    /// <summary>Root hash of the content being watched (typically published earlier via <c>Aether.Content</c>).</summary>
    public string ContentRootHash { get; set; } = string.Empty;

    /// <summary>Title displayed to participants. Hint only.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Watch mode chosen by the host.</summary>
    public WatchMode Mode { get; set; } = WatchMode.SharedFile;

    /// <summary>Last known playback position (ms). Updated whenever a sync command arrives.</summary>
    public long PositionMs { get; set; }

    /// <summary>Playback speed multiplier (1.0 = normal). Set by host.</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>True if currently playing.</summary>
    public bool IsPlaying { get; set; }

    /// <summary>Other participants known to this node. Updated as <see cref="WatchJoinPayload"/> packets arrive.</summary>
    public IReadOnlyList<string> Participants { get; set; } = Array.Empty<string>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}

/// <summary>
/// Wire payload for <see cref="AetherMesh.Protocol.PacketType.WatchSync"/>. Host emits
/// these as authoritative commands; followers apply with the RTT-compensation rule
/// in the spec (<c>local_position_at_apply ← position_ms + (now − sent_at_ms) × playback_speed</c>).
/// </summary>
public sealed class WatchSyncCommand
{
    public Guid SessionId { get; set; }
    public WatchSyncType Kind { get; set; }

    /// <summary>Authoritative playback position at <see cref="SentAtMs"/> (host's clock).</summary>
    public long PositionMs { get; set; }

    /// <summary>Speed multiplier (only meaningful for <see cref="WatchSyncType.Speed"/>; pass-through otherwise).</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>Host's wall-clock when this command was emitted.</summary>
    public long SentAtMs { get; set; }
}

/// <summary>
/// Wire payload for <see cref="AetherMesh.Protocol.PacketType.WatchReaction"/> — followers
/// can fire small reactions (emoji etc.) without disturbing host-controlled playback.
/// </summary>
public sealed class WatchReactionPayload
{
    public Guid SessionId { get; set; }

    /// <summary>Free-form reaction tag ("like", "laugh", "love", "wow", emoji codepoint).</summary>
    public string Reaction { get; set; } = string.Empty;

    /// <summary>Optional follower UHID — populated by receivers from packet source.</summary>
    public string SenderUhid { get; set; } = string.Empty;

    public long PositionMs { get; set; }
}

/// <summary>
/// Wire payload for the announce / join handshake. Carried in <see cref="AetherMesh.Protocol.PacketType.WatchSync"/>
/// with <see cref="WatchSyncCommand.Kind"/> ignored — discriminated by the JSON shape.
/// </summary>
public sealed class WatchJoinPayload
{
    public Guid SessionId { get; set; }
    public string HostUhid { get; set; } = string.Empty;
    public string ContentRootHash { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public WatchMode Mode { get; set; }
}

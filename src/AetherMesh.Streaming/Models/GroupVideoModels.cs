// SPDX-License-Identifier: MIT

namespace AetherMesh.Streaming.Models;

/// <summary>
/// Topology mode for a group video session. FullMesh means every participant
/// sends to every other participant directly. Sfu means all participants send
/// to a single relay (Selective Forwarding Unit) node that fans out.
/// </summary>
public enum VideoTopology : byte
{
    FullMesh = 0,
    Sfu = 1,
}

/// <summary>
/// Discriminator for group video signaling messages.
/// </summary>
public enum GroupVideoSignalingKind : byte
{
    /// <summary>Host creates session and broadcasts to invited UHIDs.</summary>
    Create = 0,

    /// <summary>Participant joins — host receives and updates session.</summary>
    Join = 1,

    /// <summary>Participant leaves gracefully.</summary>
    Leave = 2,

    /// <summary>Host kicks a participant.</summary>
    Kick = 3,

    /// <summary>Host notifies all participants of the SFU relay UHID.</summary>
    SfuAssigned = 4,
}

/// <summary>
/// In-memory state for a group video session. Created by the host and mirrored
/// on every participant node that has received the Create signaling.
/// </summary>
public sealed class GroupVideoSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string HostUhid { get; set; } = string.Empty;
    public List<GroupVideoParticipant> Participants { get; set; } = new();
    public VideoTopology Topology { get; set; } = VideoTopology.FullMesh;

    /// <summary>UHID of the SFU relay node, or null when topology is FullMesh.</summary>
    public string? SfuRelayUhid { get; set; }

    /// <summary>True when at least one participant has not yet left.</summary>
    public bool IsActive => Participants.Any(p => !p.HasLeft);

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
}

/// <summary>
/// State for one participant inside a <see cref="GroupVideoSession"/>.
/// </summary>
public sealed class GroupVideoParticipant
{
    public string Uhid { get; init; } = string.Empty;
    public VideoResolution Resolution { get; set; } = VideoResolution.R720p;
    public string VideoCodec { get; set; } = "H264";
    public int BitrateKbps { get; set; } = 1500;
    public bool IsMuted { get; set; }
    public bool IsVideoOff { get; set; }
    public bool HasLeft { get; set; }
    public DateTime JoinedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// JSON-serialised payload for <see cref="AetherMesh.Protocol.PacketType.GroupVideoSignaling"/> packets.
/// Uses snake_case names for cross-language interoperability.
/// </summary>
public sealed record GroupVideoSignalingMessage
{
    public GroupVideoSignalingKind Kind { get; set; }
    public Guid SessionId { get; set; }
    public string FromUhid { get; set; } = string.Empty;

    /// <summary>Target UHID, or empty string to indicate a broadcast to all session participants.</summary>
    public string ToUhid { get; set; } = string.Empty;

    /// <summary>For <see cref="GroupVideoSignalingKind.Create"/>: UHIDs of invited participants.</summary>
    public List<string>? InvitedUhids { get; set; }

    /// <summary>For <see cref="GroupVideoSignalingKind.SfuAssigned"/>: UHID of the chosen relay node.</summary>
    public string? SfuRelayUhid { get; set; }

    public VideoResolution Resolution { get; set; } = VideoResolution.R720p;
    public string VideoCodec { get; set; } = "H264";
    public int BitrateKbps { get; set; } = 1500;
}

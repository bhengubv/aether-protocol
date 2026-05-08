// SPDX-License-Identifier: MIT

using Aether.Protocol;
using Aether.Streaming.Models;

namespace Aether.Streaming;

/// <summary>
/// Coordinates multi-party (group) video calls over the Aether mesh.
///
/// Topology is auto-managed: when active participant count is below
/// <see cref="Aether.Constants.ProtocolConstants.SfuThresholdParticipants"/> the session runs
/// as FullMesh (every sender unicasts to every other participant). At or above
/// that threshold the session switches to SFU mode where every sender unicasts
/// only to the relay node which fans the frame out.
///
/// Signaling travels as <see cref="PacketType.GroupVideoSignaling"/> JSON packets;
/// video frames re-use the <see cref="PacketType.VideoFrame"/> binary format with
/// the session GUID in the first 16 bytes.
/// </summary>
public interface IGroupVideoService
{
    /// <summary>Raised on the host after <see cref="CreateAsync"/> completes, and on invited participants when they receive the Create signaling.</summary>
    event EventHandler<GroupVideoSession>? SessionCreated;

    /// <summary>Raised when a remote participant joins (host fires this; participants fire it when they receive SfuAssigned confirming their join was processed).</summary>
    event EventHandler<GroupVideoSession>? ParticipantJoined;

    /// <summary>Raised when any participant leaves or is kicked.</summary>
    event EventHandler<GroupVideoSession>? ParticipantLeft;

    /// <summary>Raised when the session topology switches between FullMesh and SFU.</summary>
    event EventHandler<GroupVideoSession>? TopologyChanged;

    /// <summary>Raised whenever a video frame arrives for any active group session.</summary>
    event EventHandler<VideoFrame>? FrameReceived;

    /// <summary>
    /// Create a new group video session and broadcast invites to <paramref name="invitedUhids"/>.
    /// The caller becomes the session host and the first participant.
    /// </summary>
    Task<GroupVideoSession> CreateAsync(
        IReadOnlyList<string> invitedUhids,
        VideoResolution resolution,
        string videoCodec,
        int bitrateKbps,
        CancellationToken ct = default);

    /// <summary>
    /// Join a session previously received via Create signaling.
    /// Returns false if the session is unknown.
    /// </summary>
    Task<bool> JoinAsync(
        Guid sessionId,
        VideoResolution resolution,
        string videoCodec,
        int bitrateKbps,
        CancellationToken ct = default);

    /// <summary>Mark this node as having left the session and notify the host.</summary>
    Task LeaveAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Host-only: remove <paramref name="participantUhid"/> from the session.
    /// Sends a Kick signaling to the target participant and re-evaluates topology.
    /// </summary>
    Task KickAsync(Guid sessionId, string participantUhid, CancellationToken ct = default);

    /// <summary>
    /// Send an encoded video frame to the session.
    /// In FullMesh mode the frame is unicast to every active non-self participant.
    /// In SFU mode the frame is sent only to the relay node.
    /// </summary>
    Task SendFrameAsync(
        Guid sessionId,
        ReadOnlyMemory<byte> encodedPayload,
        uint sequence,
        bool isKeyframe,
        CancellationToken ct = default);

    /// <summary>Dispatch an incoming mesh packet to the appropriate handler.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken ct = default);

    /// <summary>Returns all sessions in which this node has an active (non-left) participant record.</summary>
    IReadOnlyList<GroupVideoSession> GetActiveSessions();
}

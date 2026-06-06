// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Voice.Models;

namespace AetherNet.Voice;

/// <summary>
/// Group voice (3+ participants). The host invites peers, holds authority over
/// membership and key rotation, and emits <see cref="GroupSignalingKind.RotateKey"/>
/// every time membership changes so leavers cannot decrypt subsequent audio.
///
/// Frame I/O reuses the 1-to-1 wire format (<see cref="PacketType.VoiceCall"/>)
/// but each frame's encoded payload is encrypted with the current sender-key
/// before transmission and decrypted on receive.
/// </summary>
public interface IGroupVoiceCallService
{
    event EventHandler<GroupVoiceCallSession>? GroupCallInvited;
    event EventHandler<GroupVoiceCallSession>? GroupCallActive;
    event EventHandler<GroupVoiceCallSession>? GroupCallEnded;
    event EventHandler<GroupVoiceCallSession>? MembershipChanged;
    event EventHandler<VoiceFrame>? GroupFrameReceived;

    /// <summary>Host-side: start a group call by inviting an initial participant set.</summary>
    Task<GroupVoiceCallSession> StartAsync(IReadOnlyList<string> initialParticipants, string codec, int sampleRateHz, CancellationToken cancellationToken = default);

    /// <summary>Host-side: invite an additional participant (triggers a key rotation on accept).</summary>
    Task InviteAsync(Guid callId, string uhid, CancellationToken cancellationToken = default);

    /// <summary>Host-side: remove a participant (triggers a key rotation).</summary>
    Task KickAsync(Guid callId, string uhid, CancellationToken cancellationToken = default);

    /// <summary>Invitee-side: accept an invite.</summary>
    Task AcceptAsync(Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Invitee-side: decline an invite.</summary>
    Task DeclineAsync(Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Any participant: leave the call.</summary>
    Task LeaveAsync(Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Host-side: end the call for everyone.</summary>
    Task EndAsync(Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Send an encoded voice frame for a group call. Encrypted with the current sender-key, broadcast to all participants.</summary>
    Task SendFrameAsync(Guid callId, ReadOnlyMemory<byte> encodedPayload, uint sequence, bool isSilence = false, CancellationToken cancellationToken = default);

    /// <summary>Pump an inbound packet (signaling or frame) into the service.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Active group calls on this node.</summary>
    IReadOnlyList<GroupVoiceCallSession> GetActiveCalls();
}

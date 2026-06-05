// SPDX-License-Identifier: MIT

using AetherMesh.Protocol;
using AetherMesh.Voice.Models;

namespace AetherMesh.Voice;

/// <summary>
/// Coordinates 1-to-1 voice calls over the mesh. Hosts call <see cref="PlaceAsync"/>
/// to dial a peer, <see cref="AnswerAsync"/> / <see cref="DeclineAsync"/> in response
/// to <see cref="IncomingCall"/>, and pump received <see cref="PacketType.VoiceSignaling"/>
/// and <see cref="PacketType.VoiceCall"/> packets through <see cref="HandleAsync"/>.
///
/// Frame I/O (<see cref="SendFrameAsync"/> + the <see cref="FrameReceived"/> event)
/// is intentionally codec-agnostic — encoded byte arrays in, encoded byte arrays out.
/// The host owns the codec instance and calls <see cref="IVoiceCodec.Encode"/> /
/// <see cref="IVoiceCodec.Decode"/> at the boundaries.
/// </summary>
public interface IVoiceCallService
{
    /// <summary>Raised on the callee side when an Offer arrives. Host typically asks the user, then calls Answer or Decline.</summary>
    event EventHandler<VoiceCallSession>? IncomingCall;

    /// <summary>Raised on the caller side when an Answer arrives. The session has transitioned to <see cref="CallState.Connected"/>.</summary>
    event EventHandler<VoiceCallSession>? CallConnected;

    /// <summary>Raised when a call ends for any reason.</summary>
    event EventHandler<VoiceCallSession>? CallEnded;

    /// <summary>Raised when a voice frame arrives for an active call. Host decodes and routes to the audio sink.</summary>
    event EventHandler<VoiceFrame>? FrameReceived;

    /// <summary>Place an outbound call. Returns the new <see cref="VoiceCallSession"/>.</summary>
    Task<VoiceCallSession> PlaceAsync(string calleeUhid, IReadOnlyList<string> proposedCodecs, CancellationToken cancellationToken = default);

    /// <summary>Accept an incoming call. <paramref name="selectedCodec"/> must be one of the names the caller proposed.</summary>
    Task<bool> AnswerAsync(Guid callId, string selectedCodec, int sampleRateHz, CancellationToken cancellationToken = default);

    /// <summary>Decline an incoming call.</summary>
    Task DeclineAsync(Guid callId, HangupReason reason = HangupReason.Declined, CancellationToken cancellationToken = default);

    /// <summary>End an active call.</summary>
    Task HangupAsync(Guid callId, HangupReason reason = HangupReason.Normal, CancellationToken cancellationToken = default);

    /// <summary>Send an encoded voice frame for a connected call.</summary>
    Task SendFrameAsync(Guid callId, ReadOnlyMemory<byte> encodedPayload, uint sequence, bool isSilence = false, CancellationToken cancellationToken = default);

    /// <summary>Pump a received signaling or frame packet into the service.</summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Currently active sessions on this node.</summary>
    IReadOnlyList<VoiceCallSession> GetActiveCalls();
}

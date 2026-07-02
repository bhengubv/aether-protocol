// SPDX-License-Identifier: MIT

using AetherNet.Protocol;

namespace AetherNet.VideoCallControl;

/// <summary>
/// Video call-control over <see cref="PacketType.VideoCall"/> — directed ring/accept/decline/hangup
/// signalling between two peers. The caller rings a peer (minting a call id); either side then
/// accepts, declines, or hangs up. Inbound signals surface via <see cref="CallStateChanged"/>.
/// The media plane (SDP/ICE + frames) is handled separately by the streaming VideoCallService.
/// </summary>
public interface IVideoCallControlService
{
    /// <summary>Raised when a call-control signal is received from a peer.</summary>
    event EventHandler<VideoCallStateChanged>? CallStateChanged;

    /// <summary>Ring <paramref name="peerUhid"/>: mint a call id and send a directed "ring". Returns the new call id.</summary>
    Task<Guid> RingAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>Send a directed "accept" for <paramref name="callId"/> to <paramref name="peerUhid"/>. Returns delivery success.</summary>
    Task<bool> AcceptAsync(Guid callId, string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>Send a directed "decline" for <paramref name="callId"/> to <paramref name="peerUhid"/>. Returns delivery success.</summary>
    Task<bool> DeclineAsync(Guid callId, string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>Send a directed "hangup" for <paramref name="callId"/> to <paramref name="peerUhid"/>. Returns delivery success.</summary>
    Task<bool> HangupAsync(Guid callId, string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process an incoming <see cref="PacketType.VideoCall"/> packet: parse and raise
    /// <see cref="CallStateChanged"/>. Returns false for the wrong packet type or a malformed payload.
    /// </summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

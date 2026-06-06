// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Streaming.Models;

namespace AetherNet.Streaming;

/// <summary>
/// Coordinates 1-to-1 video calls. Same shape as the audio-only service in AetherNet.Voice
/// but with video-specific signaling (codec / resolution / fps / bitrate negotiation,
/// keyframe requests, quality-change notifications) and frame transport via
/// <see cref="PacketType.VideoFrame"/> packets.
/// </summary>
public interface IVideoCallService
{
    event EventHandler<VideoCallSession>? IncomingCall;
    event EventHandler<VideoCallSession>? CallConnected;
    event EventHandler<VideoCallSession>? CallEnded;
    event EventHandler<VideoFrame>? FrameReceived;

    /// <summary>Raised when the remote requests a keyframe (e.g. after observed packet loss).</summary>
    event EventHandler<Guid>? KeyframeRequested;

    /// <summary>Raised when the remote signals a quality change. Local encoder may want to follow.</summary>
    event EventHandler<VideoCallSession>? QualityChanged;

    Task<VideoCallSession> PlaceAsync(string calleeUhid, IReadOnlyList<string> videoCodecs, IReadOnlyList<string> audioCodecs, VideoResolution resolution, int targetFps, int targetBitrateKbps, CancellationToken cancellationToken = default);

    Task<bool> AnswerAsync(Guid callId, string videoCodec, string audioCodec, VideoResolution resolution, int targetFps, int targetBitrateKbps, CancellationToken cancellationToken = default);

    Task DeclineAsync(Guid callId, VideoHangupReason reason = VideoHangupReason.Declined, CancellationToken cancellationToken = default);

    Task HangupAsync(Guid callId, VideoHangupReason reason = VideoHangupReason.Normal, CancellationToken cancellationToken = default);

    Task SendFrameAsync(Guid callId, ReadOnlyMemory<byte> encodedPayload, uint sequence, bool isKeyframe, CancellationToken cancellationToken = default);

    /// <summary>Ask the remote side to send a keyframe.</summary>
    Task RequestKeyframeAsync(Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Notify the remote that local encoder is shifting target bitrate / resolution.</summary>
    Task NotifyQualityChangeAsync(Guid callId, VideoResolution resolution, int targetFps, int targetBitrateKbps, CancellationToken cancellationToken = default);

    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    IReadOnlyList<VideoCallSession> GetActiveCalls();
}

// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using Aether.Constants;
using Aether.Extensibility;
using Aether.Protocol;
using Aether.Routing;
using Aether.Streaming.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.Streaming;

/// <summary>
/// Default video-call service. Mirrors <c>Aether.Voice.VoiceCallService</c> but for video
/// frames and the richer signaling vocabulary (codec / resolution / fps / bitrate
/// negotiation, keyframe requests, quality-change notifications).
/// </summary>
public sealed class VideoCallService : IVideoCallService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IAetherIncentiveProvider _incentives;
    private readonly ILogger<VideoCallService> _logger;

    private readonly ConcurrentDictionary<Guid, VideoCallSession> _calls = new();

    public event EventHandler<VideoCallSession>? IncomingCall;
    public event EventHandler<VideoCallSession>? CallConnected;
    public event EventHandler<VideoCallSession>? CallEnded;
    public event EventHandler<VideoFrame>? FrameReceived;
    public event EventHandler<Guid>? KeyframeRequested;
    public event EventHandler<VideoCallSession>? QualityChanged;

    public VideoCallService(
        IMeshSender sender,
        IRoutingService routing,
        IAetherIncentiveProvider? incentives = null,
        ILogger<VideoCallService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<VideoCallService>.Instance;
    }

    public async Task<VideoCallSession> PlaceAsync(string calleeUhid, IReadOnlyList<string> videoCodecs, IReadOnlyList<string> audioCodecs, VideoResolution resolution, int targetFps, int targetBitrateKbps, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(calleeUhid);
        ArgumentNullException.ThrowIfNull(videoCodecs);
        if (videoCodecs.Count == 0)
            throw new ArgumentException("At least one video codec must be proposed", nameof(videoCodecs));

        var session = new VideoCallSession
        {
            CallerUhid = _sender.LocalUhid,
            CalleeUhid = calleeUhid,
            State = VideoCallState.Outgoing,
            Resolution = resolution,
            TargetFps = targetFps,
            TargetBitrateKbps = targetBitrateKbps,
        };
        _calls[session.Id] = session;

        await SendSignalingAsync(session, new VideoSignalingMessage
        {
            Kind = VideoSignalingKind.Offer,
            CallId = session.Id,
            FromUhid = _sender.LocalUhid,
            ToUhid = calleeUhid,
            ProposedVideoCodecs = videoCodecs,
            ProposedAudioCodecs = audioCodecs ?? Array.Empty<string>(),
            Resolution = resolution,
            TargetFps = targetFps,
            TargetBitrateKbps = targetBitrateKbps,
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Video call {Id} placed to {Callee} ({Res} @ {Fps}fps {Br}kbps)",
            session.Id, calleeUhid, resolution, targetFps, targetBitrateKbps);
        return session;
    }

    public async Task<bool> AnswerAsync(Guid callId, string videoCodec, string audioCodec, VideoResolution resolution, int targetFps, int targetBitrateKbps, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoCodec);
        if (!_calls.TryGetValue(callId, out var session)) return false;
        if (session.State != VideoCallState.Incoming) return false;

        session.VideoCodec = videoCodec;
        session.AudioCodec = audioCodec ?? string.Empty;
        session.Resolution = resolution;
        session.TargetFps = targetFps;
        session.TargetBitrateKbps = targetBitrateKbps;
        session.State = VideoCallState.Connected;
        session.ConnectedAt = DateTime.UtcNow;

        await SendSignalingAsync(session, new VideoSignalingMessage
        {
            Kind = VideoSignalingKind.Answer,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = session.CallerUhid,
            SelectedVideoCodec = videoCodec,
            SelectedAudioCodec = audioCodec ?? string.Empty,
            Resolution = resolution,
            TargetFps = targetFps,
            TargetBitrateKbps = targetBitrateKbps,
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Video call {Id} answered with video={Vcodec} audio={Acodec} @ {Res}",
            callId, videoCodec, audioCodec, resolution);
        return true;
    }

    public Task DeclineAsync(Guid callId, VideoHangupReason reason = VideoHangupReason.Declined, CancellationToken cancellationToken = default)
        => HangupAsync(callId, reason, cancellationToken);

    public async Task HangupAsync(Guid callId, VideoHangupReason reason = VideoHangupReason.Normal, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session)) return;
        if (session.State is VideoCallState.Ended or VideoCallState.Failed) return;

        session.State = reason == VideoHangupReason.NetworkFailure ? VideoCallState.Failed : VideoCallState.Ended;
        session.EndedAt = DateTime.UtcNow;
        session.HangupReason = reason;

        var remote = session.RemoteUhid(_sender.LocalUhid);
        if (!string.IsNullOrEmpty(remote))
        {
            await SendSignalingAsync(session, new VideoSignalingMessage
            {
                Kind = VideoSignalingKind.Hangup,
                CallId = callId,
                FromUhid = _sender.LocalUhid,
                ToUhid = remote,
                Reason = reason,
            }, cancellationToken).ConfigureAwait(false);
        }

        CallEnded?.Invoke(this, session);
        _logger.LogInformation("Video call {Id} ended ({Reason})", callId, reason);
    }

    public async Task SendFrameAsync(Guid callId, ReadOnlyMemory<byte> encodedPayload, uint sequence, bool isKeyframe, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.State != VideoCallState.Connected) return;
        var remote = session.RemoteUhid(_sender.LocalUhid);
        if (string.IsNullOrEmpty(remote)) return;

        var payload = SerializeFrame(callId, sequence, encodedPayload.Span, isKeyframe);
        var packet = new MeshPacket
        {
            Type = PacketType.VideoFrame,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = remote,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 64,
            Payload = payload,
        };

        var route = await _routing.FindRouteAsync(remote, cancellationToken).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestKeyframeAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.State != VideoCallState.Connected) return;
        var remote = session.RemoteUhid(_sender.LocalUhid);
        if (string.IsNullOrEmpty(remote)) return;

        await SendSignalingAsync(session, new VideoSignalingMessage
        {
            Kind = VideoSignalingKind.KeyframeRequest,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = remote,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyQualityChangeAsync(Guid callId, VideoResolution resolution, int targetFps, int targetBitrateKbps, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.State != VideoCallState.Connected) return;
        var remote = session.RemoteUhid(_sender.LocalUhid);
        if (string.IsNullOrEmpty(remote)) return;

        session.Resolution = resolution;
        session.TargetFps = targetFps;
        session.TargetBitrateKbps = targetBitrateKbps;

        await SendSignalingAsync(session, new VideoSignalingMessage
        {
            Kind = VideoSignalingKind.QualityChange,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = remote,
            Resolution = resolution,
            TargetFps = targetFps,
            TargetBitrateKbps = targetBitrateKbps,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Type)
        {
            case PacketType.VideoSignaling:
                await HandleSignalingAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.VideoFrame:
                HandleFrame(packet);
                break;
            default:
                _logger.LogDebug("VideoCallService.HandleAsync ignoring non-video packet type {Type}", packet.Type);
                break;
        }
    }

    public IReadOnlyList<VideoCallSession> GetActiveCalls()
        => _calls.Values
            .Where(c => c.State is VideoCallState.Outgoing or VideoCallState.Incoming or VideoCallState.Connected)
            .ToArray();

    private async Task HandleSignalingAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        VideoSignalingMessage? body;
        try
        {
            body = JsonSerializer.Deserialize<VideoSignalingMessage>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Video: failed to deserialize signaling from packet {Id}", packet.Id);
            return;
        }
        if (body is null) return;
        if (!string.Equals(body.ToUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;

        switch (body.Kind)
        {
            case VideoSignalingKind.Offer:
            {
                var session = new VideoCallSession
                {
                    Id = body.CallId,
                    CallerUhid = body.FromUhid,
                    CalleeUhid = _sender.LocalUhid,
                    State = VideoCallState.Incoming,
                    Resolution = body.Resolution,
                    TargetFps = body.TargetFps,
                    TargetBitrateKbps = body.TargetBitrateKbps,
                };
                _calls[session.Id] = session;
                IncomingCall?.Invoke(this, session);
                break;
            }
            case VideoSignalingKind.Answer:
            {
                if (_calls.TryGetValue(body.CallId, out var session) && session.State == VideoCallState.Outgoing)
                {
                    session.VideoCodec = body.SelectedVideoCodec;
                    session.AudioCodec = body.SelectedAudioCodec;
                    session.Resolution = body.Resolution;
                    session.TargetFps = body.TargetFps;
                    session.TargetBitrateKbps = body.TargetBitrateKbps;
                    session.State = VideoCallState.Connected;
                    session.ConnectedAt = DateTime.UtcNow;
                    CallConnected?.Invoke(this, session);
                }
                break;
            }
            case VideoSignalingKind.Hangup:
            case VideoSignalingKind.Cancel:
            {
                if (_calls.TryGetValue(body.CallId, out var session)
                    && session.State is not VideoCallState.Ended and not VideoCallState.Failed)
                {
                    session.State = body.Reason == VideoHangupReason.NetworkFailure ? VideoCallState.Failed : VideoCallState.Ended;
                    session.EndedAt = DateTime.UtcNow;
                    session.HangupReason = body.Reason;
                    CallEnded?.Invoke(this, session);
                }
                break;
            }
            case VideoSignalingKind.KeyframeRequest:
            {
                if (_calls.ContainsKey(body.CallId))
                    KeyframeRequested?.Invoke(this, body.CallId);
                break;
            }
            case VideoSignalingKind.QualityChange:
            {
                if (_calls.TryGetValue(body.CallId, out var session))
                {
                    session.Resolution = body.Resolution;
                    session.TargetFps = body.TargetFps;
                    session.TargetBitrateKbps = body.TargetBitrateKbps;
                    QualityChanged?.Invoke(this, session);
                }
                break;
            }
        }

        await Task.CompletedTask;
    }

    private void HandleFrame(MeshPacket packet)
    {
        var frame = TryDeserializeFrame(packet);
        if (frame is null) return;
        if (!_calls.TryGetValue(frame.CallId, out var session) || session.State != VideoCallState.Connected) return;
        FrameReceived?.Invoke(this, frame);
    }

    private async Task SendSignalingAsync(VideoCallSession session, VideoSignalingMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var remote = string.Equals(message.FromUhid, session.CallerUhid, StringComparison.Ordinal)
            ? session.CalleeUhid
            : session.CallerUhid;

        var packet = new MeshPacket
        {
            Type = PacketType.VideoSignaling,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = remote,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 32,
            Payload = payload,
        };

        var route = await _routing.FindRouteAsync(remote, cancellationToken).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);

        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Video frame payload format (cross-language stable):
    ///   [16] CallId (RFC 4122 big-endian)
    ///   [4]  Sequence (uint32 LE)
    ///   [8]  TimestampMs (int64 LE)
    ///   [1]  IsKeyframe (0/1)
    ///   [N]  EncodedPayload
    /// </summary>
    internal static byte[] SerializeFrame(Guid callId, uint sequence, ReadOnlySpan<byte> encoded, bool isKeyframe)
    {
        var buf = new byte[16 + 4 + 8 + 1 + encoded.Length];
        if (!callId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _))
            throw new InvalidOperationException("Failed to write call id");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        buf[28] = isKeyframe ? (byte)1 : (byte)0;
        encoded.CopyTo(buf.AsSpan(29));
        return buf;
    }

    internal static VideoFrame? TryDeserializeFrame(MeshPacket packet)
    {
        if (packet.Payload.Length < 29) return null;
        var span = packet.Payload.AsSpan();
        var callId = new Guid(span[..16], bigEndian: true);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
        var isKeyframe = span[28] == 1;
        var encoded = span[29..].ToArray();
        return new VideoFrame
        {
            CallId = callId,
            SenderUhid = packet.SourceUhid,
            Sequence = sequence,
            TimestampMs = timestampMs,
            IsKeyframe = isKeyframe,
            EncodedPayload = encoded,
        };
    }

    private sealed class DefaultIncentiveProvider : IAetherIncentiveProvider
    {
    }
}

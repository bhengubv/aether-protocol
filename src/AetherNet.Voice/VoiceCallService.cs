// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Extensibility;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Voice.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Voice;

/// <summary>
/// Default voice-call service. Manages per-call state, exchanges signaling messages
/// (<see cref="PacketType.VoiceSignaling"/>) over <see cref="IMeshSender"/>, and ships
/// encoded frames (<see cref="PacketType.VoiceCall"/>) along discovered routes.
///
/// Frame transport intentionally rides on <see cref="IRoutingService"/>: voice
/// continues to work over multi-hop paths the moment a route exists. If a route
/// doesn't exist (e.g. peers just met), the host can pre-warm the route by sending
/// a heartbeat or a small text message before placing the call.
/// </summary>
public sealed class VoiceCallService : IVoiceCallService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly ILogger<VoiceCallService> _logger;

    private readonly ConcurrentDictionary<Guid, VoiceCallSession> _calls = new();

    public event EventHandler<VoiceCallSession>? IncomingCall;
    public event EventHandler<VoiceCallSession>? CallConnected;
    public event EventHandler<VoiceCallSession>? CallEnded;
    public event EventHandler<VoiceFrame>? FrameReceived;

    public VoiceCallService(
        IMeshSender sender,
        IRoutingService routing,
        IAetherNetIncentiveProvider? incentives = null,
        ILogger<VoiceCallService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<VoiceCallService>.Instance;
    }

    public async Task<VoiceCallSession> PlaceAsync(string calleeUhid, IReadOnlyList<string> proposedCodecs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(calleeUhid);
        ArgumentNullException.ThrowIfNull(proposedCodecs);
        if (proposedCodecs.Count == 0)
            throw new ArgumentException("At least one codec must be proposed", nameof(proposedCodecs));

        var session = new VoiceCallSession
        {
            CallerUhid = _sender.LocalUhid,
            CalleeUhid = calleeUhid,
            State = CallState.Outgoing,
        };
        _calls[session.Id] = session;

        var offered = await SendSignalingAsync(session, new VoiceSignalingMessage
        {
            Kind = SignalingKind.Offer,
            CallId = session.Id,
            FromUhid = _sender.LocalUhid,
            ToUhid = calleeUhid,
            ProposedCodecs = proposedCodecs,
        }, cancellationToken).ConfigureAwait(false);

        if (!offered)
        {
            // No radio took the offer, so nothing is ringing anywhere. Saying so now is the whole
            // point: a call that shows "Calling…" while nothing is on the air is worse than one that
            // fails, because the person waits instead of trying something else.
            session.State = CallState.Failed;
            session.HangupReason = HangupReason.NetworkFailure;
            session.EndedAt = DateTime.UtcNow;
            _logger.LogWarning("Voice call {Id} to {Callee} never left this node — no radio took the offer",
                session.Id, calleeUhid);
            CallEnded?.Invoke(this, session);
            return session;
        }

        _logger.LogInformation("Voice call {Id} placed to {Callee}", session.Id, calleeUhid);
        return session;
    }

    public async Task<bool> AnswerAsync(Guid callId, string selectedCodec, int sampleRateHz, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selectedCodec);
        if (!_calls.TryGetValue(callId, out var session)) return false;
        if (session.State != CallState.Incoming) return false;

        session.Codec = selectedCodec;
        session.SampleRateHz = sampleRateHz;
        session.State = CallState.Connected;
        session.ConnectedAt = DateTime.UtcNow;

        await SendSignalingAsync(session, new VoiceSignalingMessage
        {
            Kind = SignalingKind.Answer,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = session.CallerUhid,
            SelectedCodec = selectedCodec,
            SampleRateHz = sampleRateHz,
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Voice call {Id} answered with codec {Codec} @ {Rate}Hz", callId, selectedCodec, sampleRateHz);
        return true;
    }

    public Task DeclineAsync(Guid callId, HangupReason reason = HangupReason.Declined, CancellationToken cancellationToken = default)
        => HangupAsync(callId, reason, cancellationToken);

    public async Task HangupAsync(Guid callId, HangupReason reason = HangupReason.Normal, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session)) return;
        if (session.State is CallState.Ended or CallState.Failed) return;

        session.State = reason == HangupReason.NetworkFailure ? CallState.Failed : CallState.Ended;
        session.EndedAt = DateTime.UtcNow;
        session.HangupReason = reason;

        var remote = session.RemoteUhid(_sender.LocalUhid);
        if (!string.IsNullOrEmpty(remote))
        {
            await SendSignalingAsync(session, new VoiceSignalingMessage
            {
                Kind = SignalingKind.Hangup,
                CallId = callId,
                FromUhid = _sender.LocalUhid,
                ToUhid = remote,
                Reason = reason,
            }, cancellationToken).ConfigureAwait(false);
        }

        CallEnded?.Invoke(this, session);
        _logger.LogInformation("Voice call {Id} ended ({Reason})", callId, reason);
    }

    public async Task SendFrameAsync(Guid callId, ReadOnlyMemory<byte> encodedPayload, uint sequence, bool isSilence = false, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.State != CallState.Connected) return;
        var remote = session.RemoteUhid(_sender.LocalUhid);
        if (string.IsNullOrEmpty(remote)) return;

        var payload = SerializeFrame(callId, sequence, encodedPayload.Span, isSilence);
        var packet = new MeshPacket
        {
            Type = PacketType.VoiceCall,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = remote,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 64,
            Payload = payload,
        };

        // Media takes the route it already knows, or goes out to everyone — it never waits to be told
        // one.
        //
        // FindRouteAsync falls through to discovery when nothing is cached: it broadcasts a route
        // request and waits RouteTimeoutMs (five seconds) for a reply. On a direct radio link there is
        // no router to answer, so every single frame paid the full timeout and then broadcast anyway.
        // Measured on two phones: 5040ms per frame, flat, against a microphone producing fifty a
        // second — so 249 of every 250 frames were discarded and the call was silent. The call itself
        // looked perfect: connected, encrypted, both microphones open, nothing in any log.
        //
        // Signalling still discovers, because an offer is worth waiting five seconds for. A voice frame
        // is not: one that arrives five seconds late is not audio, it is history.
        var route = _routing.GetCachedRoute(remote);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Type)
        {
            case PacketType.VoiceSignaling:
                await HandleSignalingAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.VoiceCall:
                HandleFrame(packet);
                break;
            default:
                _logger.LogDebug("VoiceCallService.HandleAsync ignoring non-voice packet type {Type}", packet.Type);
                break;
        }
    }

    public IReadOnlyList<VoiceCallSession> GetActiveCalls()
        => _calls.Values
            .Where(c => c.State is CallState.Outgoing or CallState.Incoming or CallState.Connected)
            .ToArray();

    private async Task HandleSignalingAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        VoiceSignalingMessage? body;
        try
        {
            body = JsonSerializer.Deserialize<VoiceSignalingMessage>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Voice: failed to deserialize signaling message from packet {Id}", packet.Id);
            return;
        }
        if (body is null) return;
        if (!string.Equals(body.ToUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;

        switch (body.Kind)
        {
            case SignalingKind.Offer:
            {
                var session = new VoiceCallSession
                {
                    Id = body.CallId,
                    CallerUhid = body.FromUhid,
                    CalleeUhid = _sender.LocalUhid,
                    State = CallState.Incoming,
                };
                _calls[session.Id] = session;
                IncomingCall?.Invoke(this, session);
                _logger.LogInformation("Incoming voice call {Id} from {Caller}", session.Id, body.FromUhid);
                break;
            }
            case SignalingKind.Answer:
            {
                if (_calls.TryGetValue(body.CallId, out var session) && session.State == CallState.Outgoing)
                {
                    session.Codec = body.SelectedCodec;
                    session.SampleRateHz = body.SampleRateHz;
                    session.State = CallState.Connected;
                    session.ConnectedAt = DateTime.UtcNow;
                    CallConnected?.Invoke(this, session);
                    _logger.LogInformation("Voice call {Id} connected with codec {Codec} @ {Rate}Hz",
                        session.Id, session.Codec, session.SampleRateHz);
                }
                break;
            }
            case SignalingKind.Hangup:
            case SignalingKind.Cancel:
            case SignalingKind.Timeout:
            {
                if (_calls.TryGetValue(body.CallId, out var session)
                    && session.State is not CallState.Ended and not CallState.Failed)
                {
                    session.State = body.Reason == HangupReason.NetworkFailure ? CallState.Failed : CallState.Ended;
                    session.EndedAt = DateTime.UtcNow;
                    session.HangupReason = body.Reason;
                    CallEnded?.Invoke(this, session);
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
        if (!_calls.TryGetValue(frame.CallId, out var session) || session.State != CallState.Connected) return;
        FrameReceived?.Invoke(this, frame);
    }

    /// <summary>
    /// Put a signalling message on the air. Returns whether it actually left this node.
    ///
    /// <para>
    /// The result used to be discarded, so an offer that reached no radio at all was
    /// indistinguishable from one that rang. A phone sat showing "Calling…" for five minutes
    /// with nothing on the air and nothing to say so.
    /// </para>
    /// </summary>
    private async Task<bool> SendSignalingAsync(VoiceCallSession session, VoiceSignalingMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var remote = string.Equals(message.FromUhid, session.CallerUhid, StringComparison.Ordinal)
            ? session.CalleeUhid
            : session.CallerUhid;

        var packet = new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = remote,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 32,
            Payload = payload,
        };

        // Signalling does not wait for discovery either, and the reason is simpler than it looks:
        // discovery's own fallback is this same broadcast. Waiting RouteTimeoutMs first cannot make a
        // packet arrive that a broadcast would not have delivered — it only delays it by five seconds.
        //
        // That delay is the whole of a call's feel. Hanging up left the other phone still sending audio
        // for seven seconds; an answer arrived so late the caller looked like it had never rung. Both
        // read as "signalling is broken" and neither was: the offer, the answer and the hangup were all
        // sitting in a route lookup for a router that does not exist on a direct radio link.
        var route = _routing.GetCachedRoute(remote);
        var delivered = route is not null
            ? await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false)
            : await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false) > 0;

        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);
        return delivered;
    }

    /// <summary>
    /// Voice frame payload format (cross-language stable):
    ///   [16] CallId (RFC 4122 big-endian)
    ///   [4]  Sequence (uint32 LE)
    ///   [8]  TimestampMs (int64 LE)
    ///   [1]  IsSilence (0/1)
    ///   [N]  EncodedPayload
    /// </summary>
    internal static byte[] SerializeFrame(Guid callId, uint sequence, ReadOnlySpan<byte> encoded, bool isSilence)
    {
        var buf = new byte[16 + 4 + 8 + 1 + encoded.Length];
        if (!callId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _))
            throw new InvalidOperationException("Failed to write call id");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        buf[28] = isSilence ? (byte)1 : (byte)0;
        encoded.CopyTo(buf.AsSpan(29));
        return buf;
    }

    internal static VoiceFrame? TryDeserializeFrame(MeshPacket packet)
    {
        if (packet.Payload.Length < 29) return null;
        var span = packet.Payload.AsSpan();
        var callId = new Guid(span[..16], bigEndian: true);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
        var isSilence = span[28] == 1;
        var encoded = span[29..].ToArray();
        return new VoiceFrame
        {
            CallId = callId,
            SenderUhid = packet.SourceUhid,
            Sequence = sequence,
            TimestampMs = timestampMs,
            IsSilence = isSilence,
            EncodedPayload = encoded,
        };
    }

    private sealed class DefaultIncentiveProvider : IAetherNetIncentiveProvider
    {
    }
}

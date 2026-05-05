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
/// Default live broadcast service. Maintains a per-stream subscriber set on the
/// publisher side and a per-stream session on the subscriber side. Segments are
/// shipped via <see cref="PacketType.StreamSegment"/> packets unicast to each
/// subscriber along discovered routes; the simple unicast fan-out is sufficient
/// for the small subscriber counts the spec targets and avoids the complexity of
/// a multicast tree at this layer.
/// </summary>
public sealed class StreamingService : IStreamingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IAetherIncentiveProvider _incentives;
    private readonly ILogger<StreamingService> _logger;

    private readonly ConcurrentDictionary<Guid, StreamSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _subscribers = new();

    public event EventHandler<StreamSession>? StreamAnnounced;
    public event EventHandler<SubscriberJoinedEventArgs>? SubscriberJoined;
    public event EventHandler<SubscriberLeftEventArgs>? SubscriberLeft;
    public event EventHandler<StreamSegment>? SegmentReceived;
    public event EventHandler<StreamSession>? StreamEnded;

    public StreamingService(
        IMeshSender sender,
        IRoutingService routing,
        IAetherIncentiveProvider? incentives = null,
        ILogger<StreamingService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<StreamingService>.Instance;
    }

    public async Task<StreamSession> StartStreamAsync(string title, string contentType, string codec, int segmentDurationMs, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        if (segmentDurationMs <= 0) segmentDurationMs = ProtocolConstants.StreamSegmentDurationMs;

        var session = new StreamSession
        {
            PublisherUhid = _sender.LocalUhid,
            Role = StreamRole.Publisher,
            State = StreamState.Live,
            Title = title,
            ContentType = contentType,
            Codec = codec,
            SegmentDurationMs = segmentDurationMs,
            StartedAt = DateTime.UtcNow,
        };
        _sessions[session.Id] = session;
        _subscribers[session.Id] = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        await BroadcastAnnounceAsync(session, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stream {Id} started — title={Title} codec={Codec}", session.Id, title, codec);
        return session;
    }

    public async Task PublishSegmentAsync(Guid streamId, ReadOnlyMemory<byte> encoded, uint sequence, bool isKeyframe, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(streamId, out var session) || session.Role != StreamRole.Publisher)
            return;
        if (session.State != StreamState.Live) return;

        var payload = SerializeSegment(streamId, sequence, encoded.Span, isKeyframe);
        if (!_subscribers.TryGetValue(streamId, out var set) || set.Count == 0) return;

        foreach (var subscriber in set.Keys)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var packet = new MeshPacket
            {
                Type = PacketType.StreamSegment,
                SourceUhid = _sender.LocalUhid,
                DestinationUhid = subscriber,
                Ttl = ProtocolConstants.DefaultTtl,
                Priority = 32,
                Payload = payload,
            };
            var route = await _routing.FindRouteAsync(subscriber, cancellationToken).ConfigureAwait(false);
            if (route is not null)
                await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
            else
                await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);

            await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);
        }
        session.HighestSegmentSequence = Math.Max(session.HighestSegmentSequence, sequence);
    }

    public async Task EndStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(streamId, out var session) || session.Role != StreamRole.Publisher) return;
        if (session.State == StreamState.Ended) return;

        session.State = StreamState.Ended;
        session.EndedAt = DateTime.UtcNow;
        await BroadcastAnnounceAsync(session, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stream {Id} ended", session.Id);
    }

    public async Task SubscribeAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(streamId, out var session)) return;
        if (session.Role == StreamRole.Publisher) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new StreamSubscribePayload
        {
            StreamId = streamId,
            LiveOnly = true,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.StreamSubscribe,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = session.PublisherUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 16,
            Payload = payload,
        };
        var route = await _routing.FindRouteAsync(session.PublisherUhid, cancellationToken).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(streamId, out var session)) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new StreamUnsubscribePayload
        {
            StreamId = streamId,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.StreamUnsubscribe,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = session.PublisherUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 16,
            Payload = payload,
        };
        var route = await _routing.FindRouteAsync(session.PublisherUhid, cancellationToken).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);

        _sessions.TryRemove(streamId, out _);
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Type)
        {
            case PacketType.StreamAnnounce:
                HandleAnnounce(packet);
                break;
            case PacketType.StreamSubscribe:
                HandleSubscribe(packet);
                break;
            case PacketType.StreamUnsubscribe:
                HandleUnsubscribe(packet);
                break;
            case PacketType.StreamSegment:
                HandleSegment(packet);
                break;
            default:
                _logger.LogDebug("StreamingService.HandleAsync ignoring non-stream packet type {Type}", packet.Type);
                break;
        }
        await Task.CompletedTask;
    }

    public IReadOnlyList<StreamSession> GetActiveStreams()
        => _sessions.Values.Where(s => s.State is StreamState.Live or StreamState.Ending).ToArray();

    private async Task BroadcastAnnounceAsync(StreamSession session, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new StreamAnnouncePayload
        {
            StreamId = session.Id,
            Title = session.Title,
            ContentType = session.ContentType,
            Codec = session.Codec,
            SegmentDurationMs = session.SegmentDurationMs,
            State = session.State,
            StartedAtMs = ((DateTimeOffset)session.StartedAt).ToUnixTimeMilliseconds(),
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.StreamAnnounce,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 0,
            Payload = payload,
        };
        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private void HandleAnnounce(MeshPacket packet)
    {
        StreamAnnouncePayload? body;
        try
        {
            body = JsonSerializer.Deserialize<StreamAnnouncePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Streaming: failed to deserialize announce from packet {Id}", packet.Id);
            return;
        }
        if (body is null) return;

        var session = _sessions.AddOrUpdate(body.StreamId,
            _ => new StreamSession
            {
                Id = body.StreamId,
                PublisherUhid = packet.SourceUhid,
                Role = StreamRole.Subscriber,
                State = body.State,
                Title = body.Title,
                ContentType = body.ContentType,
                Codec = body.Codec,
                SegmentDurationMs = body.SegmentDurationMs,
                StartedAt = body.StartedAtMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(body.StartedAtMs).UtcDateTime
                    : DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.State = body.State;
                existing.Title = body.Title;
                existing.ContentType = body.ContentType;
                existing.Codec = body.Codec;
                existing.SegmentDurationMs = body.SegmentDurationMs;
                return existing;
            });

        if (body.State is StreamState.Ended or StreamState.Ending)
        {
            session.EndedAt = DateTime.UtcNow;
            StreamEnded?.Invoke(this, session);
        }
        else
        {
            StreamAnnounced?.Invoke(this, session);
        }
    }

    private void HandleSubscribe(MeshPacket packet)
    {
        StreamSubscribePayload? body;
        try
        {
            body = JsonSerializer.Deserialize<StreamSubscribePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Streaming: failed to deserialize subscribe from packet {Id}", packet.Id);
            return;
        }
        if (body is null) return;
        if (!_subscribers.TryGetValue(body.StreamId, out var set)) return;

        if (set.TryAdd(packet.SourceUhid, 0))
        {
            SubscriberJoined?.Invoke(this, new SubscriberJoinedEventArgs
            {
                StreamId = body.StreamId,
                SubscriberUhid = packet.SourceUhid,
            });
            _logger.LogDebug("Subscriber {Sub} joined stream {Id}", packet.SourceUhid, body.StreamId);
        }
    }

    private void HandleUnsubscribe(MeshPacket packet)
    {
        StreamUnsubscribePayload? body;
        try
        {
            body = JsonSerializer.Deserialize<StreamUnsubscribePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Streaming: failed to deserialize unsubscribe from packet {Id}", packet.Id);
            return;
        }
        if (body is null) return;
        if (!_subscribers.TryGetValue(body.StreamId, out var set)) return;

        if (set.TryRemove(packet.SourceUhid, out _))
        {
            SubscriberLeft?.Invoke(this, new SubscriberLeftEventArgs
            {
                StreamId = body.StreamId,
                SubscriberUhid = packet.SourceUhid,
            });
            _logger.LogDebug("Subscriber {Sub} left stream {Id}", packet.SourceUhid, body.StreamId);
        }
    }

    private void HandleSegment(MeshPacket packet)
    {
        var segment = TryDeserializeSegment(packet);
        if (segment is null) return;
        if (!_sessions.TryGetValue(segment.StreamId, out var session) || session.Role == StreamRole.Publisher) return;

        session.HighestSegmentSequence = Math.Max(session.HighestSegmentSequence, segment.Sequence);
        SegmentReceived?.Invoke(this, segment);
    }

    /// <summary>
    /// Stream segment payload format (cross-language stable):
    ///   [16] StreamId (RFC 4122 big-endian)
    ///   [4]  Sequence (uint32 LE)
    ///   [8]  TimestampMs (int64 LE)
    ///   [1]  IsKeyframe (0/1)
    ///   [N]  EncodedPayload
    /// </summary>
    internal static byte[] SerializeSegment(Guid streamId, uint sequence, ReadOnlySpan<byte> encoded, bool isKeyframe)
    {
        var buf = new byte[16 + 4 + 8 + 1 + encoded.Length];
        if (!streamId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _))
            throw new InvalidOperationException("Failed to write stream id");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        buf[28] = isKeyframe ? (byte)1 : (byte)0;
        encoded.CopyTo(buf.AsSpan(29));
        return buf;
    }

    internal static StreamSegment? TryDeserializeSegment(MeshPacket packet)
    {
        if (packet.Payload.Length < 29) return null;
        var span = packet.Payload.AsSpan();
        var streamId = new Guid(span[..16], bigEndian: true);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
        var isKeyframe = span[28] == 1;
        var encoded = span[29..].ToArray();
        return new StreamSegment
        {
            StreamId = streamId,
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

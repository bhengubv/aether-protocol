// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Channels;

/// <summary>
/// Default named-channel pub/sub service. Publishing floods a <see cref="PacketType.ChannelMessage"/>;
/// receivers de-dup by message id, surface messages for subscribed channels, and re-flood (TTL-bounded)
/// so the message reaches subscribers multiple hops away.
/// </summary>
public sealed class ChannelMessageService : IChannelMessageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly ILogger<ChannelMessageService> _logger;

    private readonly ConcurrentDictionary<string, byte> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();

    public event EventHandler<ChannelMessageReceived>? MessageReceived;

    public ChannelMessageService(IMeshSender sender, ILogger<ChannelMessageService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<ChannelMessageService>.Instance;
    }

    /// <inheritdoc />
    public void Subscribe(string channelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        _subscriptions[channelId] = 0;
    }

    /// <inheritdoc />
    public void Unsubscribe(string channelId) => _subscriptions.TryRemove(channelId, out _);

    /// <inheritdoc />
    public IReadOnlyList<string> GetSubscriptions() => _subscriptions.Keys.ToArray();

    /// <inheritdoc />
    public async Task<int> PublishAsync(string channelId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(content);

        var payload = new ChannelMessagePayload
        {
            ChannelId = channelId,
            MessageId = Guid.NewGuid(),
            SenderUhid = _sender.LocalUhid,
            Content = content,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _seen.TryAdd(payload.MessageId, 0); // never re-handle our own message when it floods back

        var packet = new MeshPacket
        {
            Type = PacketType.ChannelMessage,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };

        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Channel {Channel} publish {Msg} to {Peers} peers", channelId, payload.MessageId, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.ChannelMessage)
            return false;

        ChannelMessagePayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ChannelMessagePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ChannelMessage from {Source}: malformed payload — dropped", packet.SourceUhid);
            return false;
        }
        if (body is null || string.IsNullOrEmpty(body.ChannelId))
            return false;

        // Flood de-duplication: only the first copy of a given message id is processed.
        if (!_seen.TryAdd(body.MessageId, 0))
            return false;

        var isOwn = string.Equals(body.SenderUhid, _sender.LocalUhid, StringComparison.Ordinal);
        if (!isOwn && _subscriptions.ContainsKey(body.ChannelId))
        {
            MessageReceived?.Invoke(this, new ChannelMessageReceived
            {
                ChannelId = body.ChannelId,
                MessageId = body.MessageId,
                SenderUhid = body.SenderUhid,
                Content = body.Content,
                SentAtMs = body.SentAtMs,
            });
        }

        // Re-flood so subscribers further out receive it — even if WE aren't subscribed (pure relay).
        if (packet.Ttl > 1 && !isOwn)
        {
            packet.Ttl--;
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}

// SPDX-License-Identifier: MIT

using System.Text.Json;
using Aether.Diagnostics;
using Aether.Dtn;
using Aether.Extensibility;
using Aether.Messaging.Models;
using Aether.Models;
using Aether.Protocol;
using Aether.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.Messaging;

/// <summary>
/// Default messaging service. Composes the existing Aether seams:
///   <see cref="IMessageEnvelopeCipher"/> for end-to-end encryption,
///   <see cref="IRoutingService"/> for next-hop discovery,
///   <see cref="IMeshSender"/> for transport handoff,
///   <see cref="IDtnService"/> (optional) for store-and-forward fallback,
///   <see cref="IAetherBackendClient"/> (optional) for cloud relay,
///   <see cref="IAetherIncentiveProvider"/> for relay accounting.
///
/// **Security rule** carried over from the private CircleAether implementation:
/// messages without a Signal session are *queued, never sent insecurely*. The
/// only exit paths from <see cref="SendAsync"/> are: encrypted ciphertext on the
/// wire, encrypted ciphertext in DTN custody, encrypted ciphertext via backend
/// relay, or "queued" — never plaintext, never a downgraded cipher.
/// </summary>
public sealed class MessagingService : IMessagingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IMessageStore _store;
    private readonly IMessageEnvelopeCipher _cipher;
    private readonly IDtnService? _dtn;
    private readonly IAetherBackendClient _backend;
    private readonly IAetherIncentiveProvider _incentives;
    private readonly MessagingOptions _options;
    private readonly ILogger<MessagingService> _logger;

    public event EventHandler<MeshMessage>? MessageReceived;
    public event EventHandler<DeliveryReceipt>? DeliveryConfirmed;
    public event EventHandler<string>? SessionRequired;

    public MessagingService(
        IMeshSender sender,
        IRoutingService routing,
        IMessageStore? store = null,
        IMessageEnvelopeCipher? cipher = null,
        IDtnService? dtn = null,
        IAetherBackendClient? backend = null,
        IAetherIncentiveProvider? incentives = null,
        MessagingOptions? options = null,
        ILogger<MessagingService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _store = store ?? new InMemoryMessageStore();
        _cipher = cipher ?? new NullMessageEnvelopeCipher();
        _dtn = dtn;
        _backend = backend ?? new DefaultBackendClient();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _options = options ?? new MessagingOptions();
        _logger = logger ?? NullLogger<MessagingService>.Instance;
    }

    public async Task<bool> SendAsync(MeshMessage message, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(message.RecipientUhid);

        if (string.IsNullOrEmpty(message.SenderUhid))
            message.SenderUhid = _sender.LocalUhid;

        var ciphertext = await _cipher.EncryptAsync(message.RecipientUhid, plaintext, cancellationToken).ConfigureAwait(false);
        if (ciphertext is null)
        {
            // No session — queue without ciphertext on the wire. Plaintext is NOT persisted.
            message.EncryptedContent = [];
            message.Status = MessageStatus.Pending;
            await _store.SaveAsync(message, cancellationToken).ConfigureAwait(false);
            SessionRequired?.Invoke(this, message.RecipientUhid);
            AetherTelemetry.MessagingMessagesQueued.Add(1);
            _logger.LogDebug("Message {Id} queued — no Signal session with {Recipient}",
                message.Id, message.RecipientUhid);
            return false;
        }

        message.EncryptedContent = ciphertext;
        message.Status = MessageStatus.Pending;
        await _store.SaveAsync(message, cancellationToken).ConfigureAwait(false);

        return await TryDeliverAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        switch (packet.Type)
        {
            case PacketType.Data:
                await HandleIncomingDataAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.Ack:
                await HandleIncomingAckAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug("MessagingService.HandleAsync ignoring non-message packet type {Type}", packet.Type);
                break;
        }
    }

    public async Task<int> ProcessOutboxAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _store.GetPendingOutboxAsync(_sender.LocalUhid, _options.MaxRetries, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0) return 0;

        var sent = 0;
        foreach (var message in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // If the message was queued because of a missing session, retry encryption.
            if (message.EncryptedContent.Length == 0)
            {
                _logger.LogDebug("Outbox: message {Id} has no ciphertext — still awaiting session, skipping",
                    message.Id);
                await _store.IncrementRetryAsync(message.Id, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await TryDeliverAsync(message, cancellationToken).ConfigureAwait(false))
            {
                sent++;
            }
            else
            {
                await _store.IncrementRetryAsync(message.Id, cancellationToken).ConfigureAwait(false);
                if (message.RetryCount + 1 >= _options.MaxRetries)
                    await _store.UpdateStatusAsync(message.Id, MessageStatus.Failed, cancellationToken).ConfigureAwait(false);
            }
        }

        if (sent > 0)
            _logger.LogDebug("Outbox: {Sent}/{Total} pending messages delivered", sent, pending.Count);

        return sent;
    }

    public Task<IReadOnlyList<MeshMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _store.GetInboxAsync(_sender.LocalUhid, limit, cancellationToken);

    public Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _store.GetOutboxAsync(_sender.LocalUhid, limit, cancellationToken);

    private async Task<bool> TryDeliverAsync(MeshMessage message, CancellationToken cancellationToken)
    {
        message.Status = MessageStatus.Sending;
        await _store.SaveAsync(message, cancellationToken).ConfigureAwait(false);

        // Tier 1: known mesh route (one or more hops).
        var route = await _routing.FindRouteAsync(message.RecipientUhid, cancellationToken).ConfigureAwait(false);
        if (route is not null)
        {
            var packet = BuildDataPacket(message);
            if (await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false))
            {
                await _store.UpdateStatusAsync(message.Id, MessageStatus.Sent, cancellationToken).ConfigureAwait(false);
                AetherTelemetry.MessagingMessagesSent.Add(1);
                _logger.LogDebug("Message {Id} sent via mesh next-hop {Hop}", message.Id, route.NextHopUhid);
                return true;
            }
        }

        // Tier 2: DTN store-and-forward (if wired and enabled).
        if (_options.EnableDtnFallback && _dtn is not null)
        {
            try
            {
                var priority = message.Priority >= Constants.ProtocolConstants.SosPriority
                    ? BundlePriority.Sos
                    : BundlePriority.Normal;
                await _dtn.CreateBundleAsync(message.RecipientUhid, message.EncryptedContent, priority, recipientLastGeohash: null, cancellationToken).ConfigureAwait(false);
                await _store.UpdateStatusAsync(message.Id, MessageStatus.Sent, cancellationToken).ConfigureAwait(false);
                AetherTelemetry.MessagingDtnFallback.Add(1);
                AetherTelemetry.MessagingMessagesSent.Add(1);
                _logger.LogDebug("Message {Id} accepted as DTN bundle for {Recipient}", message.Id, message.RecipientUhid);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DTN fallback failed for message {Id}", message.Id);
            }
        }

        // Tier 3: backend relay.
        if (_options.EnableBackendRelay)
        {
            try
            {
                if (await _backend.RelayMessageAsync(message.SenderUhid, message.RecipientUhid, message.EncryptedContent, message.Priority, cancellationToken).ConfigureAwait(false))
                {
                    await _store.UpdateStatusAsync(message.Id, MessageStatus.Sent, cancellationToken).ConfigureAwait(false);
                    AetherTelemetry.MessagingMessagesSent.Add(1);
                    _logger.LogDebug("Message {Id} accepted by backend relay for {Recipient}", message.Id, message.RecipientUhid);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Backend relay failed for message {Id}", message.Id);
            }
        }

        message.Status = MessageStatus.Pending;
        await _store.SaveAsync(message, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Message {Id} could not be delivered — staying in outbox", message.Id);
        return false;
    }

    private async Task HandleIncomingDataAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        if (!string.Equals(packet.DestinationUhid, _sender.LocalUhid, StringComparison.Ordinal))
        {
            _logger.LogDebug("Data packet {Id} not for local node — forwarding is the routing layer's job, ignoring here", packet.Id);
            return;
        }

        var plaintext = await _cipher.DecryptAsync(packet.SourceUhid, packet.Payload, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
        {
            _logger.LogDebug("Data packet {Id} from {Source} dropped — no session or decrypt failed", packet.Id, packet.SourceUhid);
            return;
        }

        var message = new MeshMessage
        {
            Id = packet.Id,
            SenderUhid = packet.SourceUhid,
            RecipientUhid = _sender.LocalUhid,
            EncryptedContent = packet.Payload,
            Status = MessageStatus.Delivered,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(packet.TimestampMs).UtcDateTime,
            Priority = packet.Priority,
        };
        await _store.SaveAsync(message, cancellationToken).ConfigureAwait(false);
        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);

        // Surface the plaintext to listeners. We do NOT persist plaintext.
        var deliveredView = new MeshMessage
        {
            Id = message.Id,
            SenderUhid = message.SenderUhid,
            RecipientUhid = message.RecipientUhid,
            EncryptedContent = plaintext,
            Status = MessageStatus.Delivered,
            CreatedAt = message.CreatedAt,
            Priority = message.Priority,
        };
        MessageReceived?.Invoke(this, deliveredView);

        if (_options.SendDeliveryAcks)
            await SendDeliveryAckAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingAckAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        if (!string.Equals(packet.DestinationUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return;

        DeliveryReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<DeliveryReceipt>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize delivery receipt from packet {Id}", packet.Id);
            return;
        }
        if (receipt is null || receipt.MessageId == Guid.Empty) return;

        var message = await _store.GetAsync(receipt.MessageId, cancellationToken).ConfigureAwait(false);
        if (message is not null)
        {
            message.Status = MessageStatus.Delivered;
            await _store.SaveAsync(message, cancellationToken).ConfigureAwait(false);
        }

        DeliveryConfirmed?.Invoke(this, receipt);
        _logger.LogDebug("Delivery confirmed for message {Id} via {Transport} ({Hops} hops, {Latency}ms)",
            receipt.MessageId, receipt.TransportType, receipt.HopCount, receipt.LatencyMs);
    }

    private async Task SendDeliveryAckAsync(MeshPacket dataPacket, CancellationToken cancellationToken)
    {
        var receipt = new DeliveryReceipt
        {
            MessageId = dataPacket.Id,
            SenderUhid = dataPacket.SourceUhid,
            RecipientUhid = _sender.LocalUhid,
            HopCount = Math.Max(1, Constants.ProtocolConstants.DefaultTtl - dataPacket.Ttl + 1),
            TransportType = "mesh",
        };

        var ack = new MeshPacket
        {
            Type = PacketType.Ack,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = dataPacket.SourceUhid,
            Ttl = Constants.ProtocolConstants.DefaultTtl,
            Priority = dataPacket.Priority,
            Payload = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions),
        };

        var route = await _routing.FindRouteAsync(dataPacket.SourceUhid, cancellationToken).ConfigureAwait(false);
        if (route is not null)
        {
            await _sender.SendAsync(ack, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // No route — ACK travels best-effort via broadcast (small price; ACK is short-lived state).
            await _sender.BroadcastAsync(ack, cancellationToken).ConfigureAwait(false);
        }
    }

    private MeshPacket BuildDataPacket(MeshMessage message)
        => new()
        {
            Id = message.Id,
            Type = PacketType.Data,
            SourceUhid = message.SenderUhid,
            DestinationUhid = message.RecipientUhid,
            Ttl = Constants.ProtocolConstants.DefaultTtl,
            Priority = message.Priority,
            Payload = message.EncryptedContent,
        };

    private sealed class DefaultBackendClient : IAetherBackendClient
    {
    }

    private sealed class DefaultIncentiveProvider : IAetherIncentiveProvider
    {
    }
}

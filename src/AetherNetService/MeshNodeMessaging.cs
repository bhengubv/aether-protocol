// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Identity;   // AetherNetTag
using AetherNet.Mesh;
using AetherNetNodeService;
using AetherNetNodeService.Host;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// What a connected app's send and inbox become inside AetherNetService. A send hands the app's payload to the
/// reliable core under the app's own message id; the core seals it (Signal), picks the radio and confirms
/// delivery, and the confirmation goes back to the app under that same id (<see cref="Delivered"/>).
///
/// <para>
/// When the core cannot send yet — no secure session with the peer, or no path to them right now — this holds
/// the message instead of losing it (the core keeps no plaintext for a message it could not seal), and sends it
/// the moment it can: when the session is built (<see cref="MeshSessionKeeper.SessionEstablished"/>), when the
/// peer's link comes up, or when the peer is heard from. The app is told "queued" once and never sends twice.
/// Held messages live in memory: a node restart loses them, and the app still shows them as sent.
/// </para>
/// </summary>
internal sealed class MeshNodeMessaging : INodeMessaging, IDisposable
{
    private const int Window = 200;

    private readonly IMessagingService _messaging;
    private readonly MeshSessionKeeper _sessions;
    private readonly IRadioMesh _radio;
    private readonly ILogger? _log;
    private readonly List<InboundMessage> _recent = new();
    private readonly Dictionary<string, List<Held>> _held = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public MeshNodeMessaging(IMessagingService messaging, MeshSessionKeeper sessions, IRadioMesh radio,
        ILogger<MeshNodeMessaging>? log = null)
    {
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _log = log;

        _messaging.MessageReceived += OnReceived;
        _messaging.DeliveryConfirmed += OnDeliveryConfirmed;
        _sessions.SessionEstablished += OnReachable;
        _radio.PeerLinked += OnReachable;
    }

    public event Action<InboundMessage>? Inbound;

    public event Action<Guid>? Delivered;

    public async Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
    {
        var peer = to.Value;
        var bytes = payload.ToArray();

        if (await TrySendAsync(peer, bytes, messageId, cancellationToken).ConfigureAwait(false))
        {
            return OutboundResult.Sent;
        }

        // Not yet. The core has already asked for a session if that was the reason (SessionRequired); hold the
        // message until one of the triggers above says it can go.
        lock (_gate)
        {
            if (!_held.TryGetValue(peer, out var list))
            {
                _held[peer] = list = new List<Held>();
            }

            list.Add(new Held(bytes, messageId));
        }

        _log?.LogInformation("Holding message {Id} for {Peer} until there is a session and a path", messageId, peer);
        return OutboundResult.Queued;
    }

    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<InboundMessage> recent = _recent
                .AsEnumerable()
                .Reverse()
                .Take(limit <= 0 ? 0 : limit)
                .ToList();
            return Task.FromResult(recent);
        }
    }

    private Task<bool> TrySendAsync(string peer, byte[] payload, Guid messageId, CancellationToken cancellationToken)
        => _messaging.SendAsync(
            new MeshMessage { Id = messageId, RecipientUhid = peer, MessageType = "node" },
            payload,
            cancellationToken);

    private void OnReachable(string peer) => _ = FlushAsync(peer);

    /// <summary>Send what is held for this peer, oldest first, stopping at the first that still cannot go.</summary>
    private async Task FlushAsync(string peer)
    {
        if (string.IsNullOrEmpty(peer)) return;

        // One flush at a time, or two triggers arriving together would send the same message twice.
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Held[] waiting;
            lock (_gate)
            {
                if (!_held.TryGetValue(peer, out var list) || list.Count == 0) return;
                waiting = list.ToArray();
            }

            foreach (var held in waiting)
            {
                if (!await TrySendAsync(peer, held.Payload, held.Id, CancellationToken.None).ConfigureAwait(false))
                {
                    return;   // still cannot — keep this one and everything after it, in order
                }

                lock (_gate)
                {
                    _held[peer].Remove(held);
                }

                _log?.LogInformation("Sent held message {Id} to {Peer}", held.Id, peer);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not send the messages held for {Peer}", peer);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void OnReceived(object? sender, MeshMessage message)
    {
        var from = AetherNetTag.TryParse(message.SenderUhid, out var tag) ? tag : default;
        var inbound = new InboundMessage(
            from,
            message.EncryptedContent, // at delivery the "encrypted" view carries the decrypted plaintext
            string.IsNullOrEmpty(message.MessageType) ? "message" : message.MessageType,
            new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAt, DateTimeKind.Utc)),
            message.Id);

        lock (_gate)
        {
            _recent.Add(inbound);
            if (_recent.Count > Window)
            {
                _recent.RemoveAt(0);
            }
        }

        Inbound?.Invoke(inbound);

        // Hearing from them means there is a path and a session: anything held for them can go now.
        _ = FlushAsync(message.SenderUhid);
    }

    private void OnDeliveryConfirmed(object? sender, DeliveryReceipt receipt) => Delivered?.Invoke(receipt.MessageId);

    public void Dispose()
    {
        _messaging.MessageReceived -= OnReceived;
        _messaging.DeliveryConfirmed -= OnDeliveryConfirmed;
        _sessions.SessionEstablished -= OnReachable;
        _radio.PeerLinked -= OnReachable;
    }

    private sealed record Held(byte[] Payload, Guid Id);
}
#endif

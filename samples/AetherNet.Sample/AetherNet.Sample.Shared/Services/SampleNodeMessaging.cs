// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Node;
using AetherNet.Node.Host;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The node's messaging seam over the real <see cref="IMessagingService"/>. Send wraps the payload in a
/// MeshMessage addressed by tag and hands it to the messaging core; inbound is the decrypted plaintext the
/// core surfaces on delivery (which it deliberately never persists), kept here in a small recent window for
/// a bound consumer to read back. The wire format, the Signal ratchet and the ERID resolution all stay in
/// the core — this seam deals only in tags and application payloads.
/// </summary>
public sealed class SampleNodeMessaging : INodeMessaging, IDisposable
{
    private const int Window = 200;

    private readonly IMessagingService _messaging;
    private readonly List<InboundMessage> _recent = new();
    private readonly object _gate = new();

    public SampleNodeMessaging(IMessagingService messaging)
    {
        _messaging = messaging;
        _messaging.MessageReceived += OnReceived;
    }

    public event Action<InboundMessage>? Inbound;

    public async Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var message = new MeshMessage { RecipientUhid = to.Value, MessageType = "node" };
        var sent = await _messaging.SendAsync(message, payload.ToArray(), cancellationToken).ConfigureAwait(false);
        return sent ? OutboundResult.Sent : OutboundResult.Queued;
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
    }

    public void Dispose() => _messaging.MessageReceived -= OnReceived;
}

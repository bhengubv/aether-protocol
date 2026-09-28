// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Protocol;

namespace AetherNet.Node.Client;

/// <summary>
/// An app's <see cref="IMessagingService"/> answered by the node it is connected to — what lets a thin client
/// keep its own messaging code (conversations, receipts, the screens) while the node does everything that needs
/// radios or keys.
///
/// <para>
/// A send goes to the node under the app's own message id; the node seals it, holds it until there is a session
/// and a path, and delivers it. A message for this app arrives as <see cref="MessageReceived"/>, already opened
/// by the node, and the node's delivery confirmation arrives as <see cref="DeliveryConfirmed"/> under the id the
/// app sent with. <see cref="SessionRequired"/> and <see cref="DecryptFailed"/> never fire: sessions are the
/// node's job, and it does them itself. No packet ever reaches an app with no radios, so
/// <see cref="HandleAsync"/> has nothing to do.
/// </para>
/// </summary>
public sealed class NodeBackedMessaging : IMessagingService, IAetherNodeEvents, IDisposable
{
    private readonly IAetherNodeClient _node;
    private readonly IDisposable _subscription;

    public NodeBackedMessaging(IAetherNodeClient node)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _subscription = _node.Subscribe(this);
    }

    public event EventHandler<MeshMessage>? MessageReceived;

    public event EventHandler<DeliveryReceipt>? DeliveryConfirmed;

    public event EventHandler<string>? SessionRequired
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? DecryptFailed
    {
        add { }
        remove { }
    }

    public async Task<bool> SendAsync(MeshMessage message, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!AetherNetTag.TryParse(message.RecipientUhid, out var to))
        {
            return false;
        }

        var result = await _node.SendAsync(to, plaintext ?? [], message.Id, cancellationToken).ConfigureAwait(false);

        // Sent, or held by the node to send when it can: either way it is the node's now, and the confirmation
        // will come back under this message's id.
        return result.Accepted;
    }

    public Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> ProcessOutboxAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public async Task<IReadOnlyList<MeshMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var inbox = await _node.GetInboxAsync(limit, cancellationToken).ConfigureAwait(false);
        var list = new List<MeshMessage>(inbox.Count);
        foreach (var message in inbox)
        {
            list.Add(ToMesh(message));
        }

        return list;
    }

    public Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MeshMessage>>(Array.Empty<MeshMessage>());

    void IAetherNodeEvents.OnInbound(InboundMessage message) => MessageReceived?.Invoke(this, ToMesh(message));

    void IAetherNodeEvents.OnLinkChanged(NodeLinkStatus status) { }

    void IAetherNodeEvents.OnGrantChanged(GrantState state) { }

    void IAetherNodeEvents.OnDelivered(Guid messageId)
        => DeliveryConfirmed?.Invoke(this, new DeliveryReceipt { MessageId = messageId });

    private static MeshMessage ToMesh(InboundMessage message) => new()
    {
        Id = message.Id,
        SenderUhid = message.From.Value ?? string.Empty,
        EncryptedContent = message.Payload.ToArray(),   // the node has already opened it — this is plaintext
        MessageType = message.Kind,
        CreatedAt = message.ReceivedAt.UtcDateTime,
    };

    public void Dispose() => _subscription.Dispose();
}

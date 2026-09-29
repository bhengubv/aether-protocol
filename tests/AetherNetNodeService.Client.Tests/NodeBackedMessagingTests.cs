// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Messaging.Models;
using Xunit;

namespace AetherNetNodeService.Client.Tests;

/// <summary>
/// A thin client keeps its own messaging code and lets the node do the radios and keys. These pin the seam
/// between them: a send carries the app's own message id to the node, a message the node opened arrives as the
/// app's message, and the node's delivery confirmation arrives as the app's receipt — under that same id.
/// </summary>
public class NodeBackedMessagingTests
{
    private static AetherNetTag Parse(string value)
    {
        Assert.True(AetherNetTag.TryParse(value, out var tag));
        return tag;
    }

    private sealed class FakeNode : IAetherNodeClient
    {
        public OutboundResult Result = OutboundResult.Sent;
        public IAetherNodeEvents? Listener;
        public (AetherNetTag To, byte[] Payload, Guid Id)? Sent;

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
        {
            Sent = (to, payload.ToArray(), messageId);
            return Task.FromResult(Result);
        }

        public IDisposable Subscribe(IAetherNodeEvents listener)
        {
            Listener = listener;
            return new Noop();
        }

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(AetherNetTag));
        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(System.Array.Empty<byte>());
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.FromResult(System.Array.Empty<byte>());
        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InboundMessage>>(System.Array.Empty<InboundMessage>());
        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default) => Task.FromResult(NodeLinkStatus.Offline);
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task A_send_carries_the_apps_message_id_to_the_node()
    {
        var node = new FakeNode();
        using var messaging = new NodeBackedMessaging(node);
        var message = new MeshMessage { Id = Guid.NewGuid(), RecipientUhid = "9BWNJ-QPXG8" };

        var accepted = await messaging.SendAsync(message, new byte[] { 1, 2, 3 });

        Assert.True(accepted);
        Assert.Equal(Parse("9BWNJ-QPXG8"), node.Sent!.Value.To);
        Assert.Equal(new byte[] { 1, 2, 3 }, node.Sent.Value.Payload);
        Assert.Equal(message.Id, node.Sent.Value.Id);
    }

    [Fact]
    public async Task A_message_the_node_holds_to_send_later_still_counts_as_accepted()
    {
        var node = new FakeNode { Result = OutboundResult.Queued };
        using var messaging = new NodeBackedMessaging(node);

        Assert.True(await messaging.SendAsync(new MeshMessage { RecipientUhid = "9BWNJ-QPXG8" }, new byte[] { 1 }));
    }

    [Fact]
    public async Task A_recipient_that_is_not_a_tag_is_refused_without_bothering_the_node()
    {
        var node = new FakeNode();
        using var messaging = new NodeBackedMessaging(node);

        Assert.False(await messaging.SendAsync(new MeshMessage { RecipientUhid = "not a tag" }, new byte[] { 1 }));
        Assert.Null(node.Sent);
    }

    [Fact]
    public void A_message_the_node_opened_arrives_as_the_apps_message()
    {
        var node = new FakeNode();
        using var messaging = new NodeBackedMessaging(node);
        MeshMessage? received = null;
        messaging.MessageReceived += (_, m) => received = m;
        var id = Guid.NewGuid();

        node.Listener!.OnInbound(new InboundMessage(Parse("9BWNJ-QPXG8"), new byte[] { 7, 8 }, "node", DateTimeOffset.UnixEpoch, id));

        Assert.NotNull(received);
        Assert.Equal(id, received!.Id);
        Assert.Equal("9BWNJ-QPXG8", received.SenderUhid);
        Assert.Equal(new byte[] { 7, 8 }, received.EncryptedContent);
        Assert.Equal("node", received.MessageType);
    }

    [Fact]
    public void The_nodes_delivery_confirmation_arrives_as_the_apps_receipt()
    {
        var node = new FakeNode();
        using var messaging = new NodeBackedMessaging(node);
        DeliveryReceipt? receipt = null;
        messaging.DeliveryConfirmed += (_, r) => receipt = r;
        var id = Guid.NewGuid();

        node.Listener!.OnDelivered(id);

        Assert.Equal(id, receipt!.MessageId);
    }
}

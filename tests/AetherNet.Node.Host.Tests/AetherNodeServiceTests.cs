// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNet.Node.Host.Tests;

public class AetherNodeServiceTests
{
    private sealed class FakeIdentity : INodeIdentity
    {
        public bool Locked;
        public byte[] Pub { get; } = new byte[32];
        public AetherNetTag Tag { get; } = AetherNetTag.FromPublicKey(new byte[32]);

        public ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken cancellationToken = default)
            => Locked ? throw new NodeIdentityUnavailableException("locked") : ValueTask.FromResult(Tag);

        public ValueTask<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
            => Locked ? throw new NodeIdentityUnavailableException("locked") : ValueTask.FromResult(Pub);

        public ValueTask<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new[] { (byte)data.Length });

        public ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new byte[32]);
    }

    private sealed class FakeMessaging : INodeMessaging
    {
        public List<(AetherNetTag To, byte[] Payload)> Sent { get; } = new();
        public event Action<InboundMessage>? Inbound;

        public void RaiseInbound(InboundMessage message) => Inbound?.Invoke(message);

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((to, payload.ToArray()));
            return Task.FromResult(OutboundResult.Queued);
        }

        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InboundMessage>>(System.Array.Empty<InboundMessage>());
    }

    private sealed class FakeLink : INodeLinkSource
    {
        private NodeLinkStatus _current = NodeLinkStatus.Offline;
        public event Action? Changed;
        public NodeLinkStatus Current => _current;
        public void Set(NodeLinkStatus status) { _current = status; Changed?.Invoke(); }
    }

    private sealed class Recorder : IAetherNodeEvents
    {
        public List<InboundMessage> Inbound { get; } = new();
        public List<NodeLinkStatus> Links { get; } = new();
        public List<GrantState> Grants { get; } = new();
        public void OnInbound(InboundMessage message) => Inbound.Add(message);
        public void OnLinkChanged(NodeLinkStatus status) => Links.Add(status);
        public void OnGrantChanged(GrantState state) => Grants.Add(state);
    }

    private static AetherNodeService NewService(out FakeIdentity id, out FakeMessaging msg, out FakeLink link)
    {
        id = new FakeIdentity();
        msg = new FakeMessaging();
        link = new FakeLink();
        return new AetherNodeService(id, msg, link);
    }

    [Fact]
    public async Task GetTag_returns_the_identity_tag()
    {
        var svc = NewService(out var id, out _, out _);
        Assert.Equal(id.Tag, await svc.GetTagAsync());
    }

    [Fact]
    public async Task A_locked_node_reports_unavailable_not_absent()
    {
        var svc = NewService(out var id, out _, out _);
        id.Locked = true;
        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => svc.GetTagAsync());
        Assert.Equal(AetherNodeErrorCode.NodeUnavailable, ex.Code);
    }

    [Fact]
    public async Task Sign_delegates_to_the_identity_and_never_exposes_a_key()
    {
        var svc = NewService(out _, out _, out _);
        var sig = await svc.SignAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 3 }, sig);
    }

    [Fact]
    public async Task Send_delegates_to_messaging_addressed_by_tag()
    {
        var svc = NewService(out _, out var msg, out _);
        var to = AetherNetTag.Parse("ABCDE-FGHJK");
        var result = await svc.SendAsync(to, new byte[] { 9 });
        Assert.True(result.Accepted);
        Assert.Single(msg.Sent);
        Assert.Equal(to, msg.Sent[0].To);
    }

    [Fact]
    public async Task GetLink_returns_the_current_status()
    {
        var svc = NewService(out _, out _, out var link);
        link.Set(new NodeLinkStatus(true, "Wi-Fi Direct", System.Array.Empty<RadioStatus>()));
        var status = await svc.GetLinkAsync();
        Assert.True(status.Linked);
        Assert.Equal("Wi-Fi Direct", status.Radio);
    }

    [Fact]
    public void Subscribe_delivers_inbound_and_link_changes_until_disposed()
    {
        var svc = NewService(out _, out var msg, out var link);
        var recorder = new Recorder();
        var subscription = svc.Subscribe(recorder);

        msg.RaiseInbound(new InboundMessage(default, new byte[] { 1 }, "text", System.DateTimeOffset.UnixEpoch, System.Guid.Empty));
        link.Set(new NodeLinkStatus(true, "BLE", System.Array.Empty<RadioStatus>()));
        Assert.Single(recorder.Inbound);
        Assert.Single(recorder.Links);
        Assert.True(recorder.Links[0].Linked);

        subscription.Dispose();
        msg.RaiseInbound(new InboundMessage(default, new byte[] { 2 }, "text", System.DateTimeOffset.UnixEpoch, System.Guid.Empty));
        link.Set(NodeLinkStatus.Offline);
        Assert.Single(recorder.Inbound);
        Assert.Single(recorder.Links);
    }
}

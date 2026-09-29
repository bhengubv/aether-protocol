// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

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
        public List<Guid> SentIds { get; } = new();
        public event Action<InboundMessage>? Inbound;
        public event Action<Guid>? Delivered;

        public void RaiseInbound(InboundMessage message) => Inbound?.Invoke(message);
        public void RaiseDelivered(Guid messageId) => Delivered?.Invoke(messageId);

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
        {
            Sent.Add((to, payload.ToArray()));
            SentIds.Add(messageId);
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
        public List<Guid> Delivered { get; } = new();
        public void OnInbound(InboundMessage message) => Inbound.Add(message);
        public void OnLinkChanged(NodeLinkStatus status) => Links.Add(status);
        public void OnGrantChanged(GrantState state) => Grants.Add(state);
        public void OnDelivered(Guid messageId) => Delivered.Add(messageId);
    }

    private sealed class FakeMeeting : INodeMeeting
    {
        public List<IReadOnlyList<NodeContact>> Calls { get; } = new();
        public void Meet(IReadOnlyList<NodeContact> contacts) => Calls.Add(contacts);
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

    private static AetherNetTag ParseTag(string value)
    {
        Assert.True(AetherNetTag.TryParse(value, out var tag));
        return tag;
    }

    [Fact]
    public async Task Send_hands_the_apps_message_id_to_the_messaging_seam()
    {
        var svc = NewService(out _, out var msg, out _);
        var id = System.Guid.NewGuid();

        await svc.SendAsync(ParseTag("9BWNJ-QPXG8"), new byte[] { 7 }, id);

        Assert.Equal(id, Assert.Single(msg.SentIds));
    }

    [Fact]
    public void A_confirmed_delivery_reaches_a_subscriber_by_its_message_id()
    {
        var svc = NewService(out _, out var msg, out _);
        var recorder = new Recorder();
        using var subscription = svc.Subscribe(recorder);
        var id = System.Guid.NewGuid();

        msg.RaiseDelivered(id);

        Assert.Equal(id, Assert.Single(recorder.Delivered));
    }

    [Fact]
    public async Task Meet_hands_the_contacts_to_the_radios()
    {
        var meeting = new FakeMeeting();
        var svc = new AetherNodeService(new FakeIdentity(), new FakeMessaging(), new FakeLink(), meeting);
        var contacts = new[] { new NodeContact(ParseTag("9BWNJ-QPXG8"), new byte[] { 1 }, true) };

        await svc.MeetAsync(contacts);

        Assert.Same(contacts, Assert.Single(meeting.Calls));
    }

    [Fact]
    public async Task Meet_is_a_quiet_no_op_on_a_host_with_no_radios()
    {
        var svc = NewService(out _, out _, out _);

        await svc.MeetAsync(new[] { new NodeContact(ParseTag("9BWNJ-QPXG8"), null, false) });
    }

    /// <summary>The recovery the service hands the phrase out of — one answer, or one way of failing.</summary>
    private sealed class FakeRecovery : INodeIdentityRecovery
    {
        public Exception? Fails;

        public ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default)
            => Fails is { } ex ? ValueTask.FromException<string>(ex) : ValueTask.FromResult("twenty four words");

        public ValueTask<AetherNetTag> AdoptSeedAsync(byte[] seed, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<AetherNetTag> RestoreFromPhraseAsync(string recoveryPhrase, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static AetherNodeService WithRecovery(FakeRecovery recovery)
        => new(new FakeIdentity(), new FakeMessaging(), new FakeLink(), null, recovery);

    [Fact]
    public async Task The_recovery_phrase_comes_from_the_same_identity()
    {
        Assert.Equal("twenty four words", await WithRecovery(new FakeRecovery()).GetRecoveryPhraseAsync());
    }

    [Fact]
    public async Task A_locked_identity_is_unavailable_and_a_missing_one_is_absent()
    {
        var locked = await Assert.ThrowsAsync<AetherNodeException>(() =>
            WithRecovery(new FakeRecovery { Fails = new NodeIdentityUnavailableException("locked") }).GetRecoveryPhraseAsync());
        var missing = await Assert.ThrowsAsync<AetherNodeException>(() =>
            WithRecovery(new FakeRecovery { Fails = new InvalidOperationException("none") }).GetRecoveryPhraseAsync());

        Assert.Equal(AetherNodeErrorCode.NodeUnavailable, locked.Code);
        Assert.Equal(AetherNodeErrorCode.IdentityAbsent, missing.Code);
    }

    [Fact]
    public async Task A_host_with_no_recovery_refuses_rather_than_guessing()
    {
        var svc = NewService(out _, out _, out _);

        await Assert.ThrowsAsync<AetherNodeException>(() => svc.GetRecoveryPhraseAsync());
    }
}

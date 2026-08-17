// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

public class ChatServiceTests
{
    private const string Me = "KXJB7-MN2P4";
    private const string Them = "DY5CF-84G9T";

    private sealed class Rig : IDisposable
    {
        public AetherStore Store { get; } = AetherStore.InMemory();
        public FakeSignalProtocol Signal { get; } = new();
        public FakePreKeyExchange PreKeys { get; } = new();
        public FakeRadioMesh Radio { get; } = new(Me);
        public ChatService Chat { get; }

        public Rig()
        {
            Chat = new ChatService(Store, new FakeIdentity(Me), Signal, PreKeys, Radio);
        }

        /// <summary>Bring the link and the session up, the way a completed handshake does.</summary>
        public Rig Connected()
        {
            Signal.OpenSessionWith(Them);
            Radio.Link();
            return this;
        }

        public ChatMessage? Last(string peer) => Store.GetMessages(peer).LastOrDefault();

        public void Dispose() => Store.Dispose();
    }

    /// <summary>
    /// Wait for something that arrives on its own. Receipts and inbound messages are handled off the
    /// caller's thread — as they are on a radio — so a test has to wait for the outcome rather than
    /// assume it has already happened. Bounded, so a genuine failure still fails quickly.
    /// </summary>
    private static async Task<bool> Eventually(Func<bool> condition, int withinMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(withinMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    // ── Nothing leaves without a session ──────────────────────────────────────

    [Fact]
    public async Task SendAsync_keeps_a_message_pending_when_there_is_no_session()
    {
        using var rig = new Rig();
        rig.Radio.Link();                       // linked, but no session yet

        await rig.Chat.SendAsync(Them, "hello");

        Assert.Equal(ChatMessage.Pending, rig.Last(Them)!.State);
    }

    [Fact]
    public async Task SendAsync_puts_nothing_on_the_radio_without_a_session()
    {
        using var rig = new Rig();
        rig.Radio.Link();

        await rig.Chat.SendAsync(Them, "hello");

        Assert.Empty(rig.Radio.Sent);
    }

    [Fact]
    public async Task SendAsync_stores_the_message_even_when_it_cannot_go()
    {
        using var rig = new Rig();

        await rig.Chat.SendAsync(Them, "hello");

        Assert.Equal("hello", rig.Last(Them)!.Body);
    }

    // ── Sent is not delivered ─────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_marks_a_message_sent_once_the_radio_takes_it()
    {
        using var rig = new Rig().Connected();

        await rig.Chat.SendAsync(Them, "hello");

        Assert.Equal(ChatMessage.Sent, rig.Last(Them)!.State);
    }

    [Fact]
    public async Task SendAsync_does_not_claim_delivery_before_a_receipt()
    {
        using var rig = new Rig().Connected();

        await rig.Chat.SendAsync(Them, "hello");

        Assert.NotEqual(ChatMessage.Delivered, rig.Last(Them)!.State);
    }

    [Fact]
    public async Task SendAsync_leaves_a_message_pending_when_the_radio_refuses()
    {
        using var rig = new Rig().Connected();
        rig.Radio.CanSend = false;              // the link looks up but carries nothing

        await rig.Chat.SendAsync(Them, "hello");

        Assert.Equal(ChatMessage.Pending, rig.Last(Them)!.State);
    }

    // ── Two phones ────────────────────────────────────────────────────────────

    private sealed class Pair : IDisposable
    {
        public Rig A { get; } = new();
        public AetherStore StoreB { get; } = AetherStore.InMemory();
        public FakeSignalProtocol SignalB { get; } = new();
        public FakePreKeyExchange PreKeysB { get; } = new();
        public FakeRadioMesh RadioB { get; } = new(Them);
        public ChatService ChatB { get; }

        public Pair()
        {
            ChatB = new ChatService(StoreB, new FakeIdentity(Them), SignalB, PreKeysB, RadioB);

            A.Radio.Peer = RadioB;
            RadioB.Peer = A.Radio;

            A.Signal.OpenSessionWith(Them);
            SignalB.OpenSessionWith(Me);
            A.Radio.Link();
            RadioB.Link();
        }

        public void Dispose() { A.Dispose(); StoreB.Dispose(); }
    }

    [Fact]
    public async Task A_message_arrives_on_the_other_phone()
    {
        using var pair = new Pair();

        await pair.A.Chat.SendAsync(Them, "no tower no wifi");

        var received = pair.StoreB.GetMessages(Me).LastOrDefault();
        Assert.Equal("no tower no wifi", received?.Body);
    }

    [Fact]
    public async Task A_received_message_is_marked_received()
    {
        using var pair = new Pair();

        await pair.A.Chat.SendAsync(Them, "hello");

        Assert.Equal(ChatMessage.Received, pair.StoreB.GetMessages(Me).Last().State);
    }

    [Fact]
    public async Task A_receipt_comes_back_and_the_sender_shows_delivered()
    {
        using var pair = new Pair();

        await pair.A.Chat.SendAsync(Them, "hello");

        var delivered = await Eventually(() =>
            pair.A.Store.GetMessages(Them).Last().State == ChatMessage.Delivered);

        Assert.True(delivered,
            $"no receipt came back — the message is still '{pair.A.Store.GetMessages(Them).Last().State}'");
    }

    [Fact]
    public async Task The_same_message_arriving_twice_is_not_shown_twice()
    {
        using var pair = new Pair();

        await pair.A.Chat.SendAsync(Them, "hello");
        var packet = pair.A.Radio.Sent[0];
        pair.RadioB.Deliver(packet);            // a retry of the identical message
        await Task.Delay(50);

        Assert.Single(pair.StoreB.GetMessages(Me), m => m.Body == "hello");
    }

    // ── Recovering a stalled conversation ─────────────────────────────────────

    [Fact]
    public async Task A_pending_message_goes_out_when_the_session_arrives()
    {
        using var pair = new Pair();
        pair.A.Radio.CanSend = false;
        await pair.A.Chat.SendAsync(Them, "held back");
        Assert.Equal(ChatMessage.Pending, pair.A.Store.GetMessages(Them).Last().State);

        pair.A.Radio.CanSend = true;
        await pair.A.Chat.FlushAsync(Them);

        Assert.Equal("held back", pair.StoreB.GetMessages(Me).Last().Body);
    }

    [Fact]
    public async Task Everything_held_back_goes_out_in_order()
    {
        using var pair = new Pair();
        pair.A.Radio.CanSend = false;
        await pair.A.Chat.SendAsync(Them, "first");
        await pair.A.Chat.SendAsync(Them, "second");

        pair.A.Radio.CanSend = true;
        await pair.A.Chat.FlushAsync(Them);

        var arrived = pair.StoreB.GetMessages(Me).Select(m => m.Body).ToArray();
        Assert.Equal(["first", "second"], arrived);
    }

    // ── Groups ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGroupAsync_puts_the_group_in_the_chat_list()
    {
        using var rig = new Rig().Connected();

        var group = await rig.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        Assert.Contains(rig.Chat.Groups(), g => g.Id == group.Id);
    }

    [Fact]
    public async Task CreateGroupAsync_counts_the_creator_as_a_member()
    {
        using var rig = new Rig().Connected();

        var group = await rig.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        Assert.Contains(Me, rig.Chat.GroupMembers(group.Id));
    }

    [Fact]
    public async Task A_group_reaches_the_other_phone()
    {
        using var pair = new Pair();

        var group = await pair.A.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        Assert.Equal("Load-shedding crew", pair.ChatB.Group(group.Id)?.Name);
    }

    [Fact]
    public async Task A_group_message_reaches_the_other_phone()
    {
        using var pair = new Pair();
        var group = await pair.A.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        await pair.A.Chat.SendToGroupAsync(group.Id, "power is out");

        Assert.Equal("power is out", pair.StoreB.GetMessages(group.Id).Last().Body);
    }

    [Fact]
    public async Task A_group_message_says_who_wrote_it()
    {
        using var pair = new Pair();
        var group = await pair.A.Chat.CreateGroupAsync("Load-shedding crew", [Them]);

        await pair.A.Chat.SendToGroupAsync(group.Id, "power is out");

        Assert.Equal(Me, pair.StoreB.GetMessages(group.Id).Last().SenderTag);
    }

    [Fact]
    public async Task A_group_message_stays_pending_when_nobody_can_be_reached()
    {
        using var rig = new Rig().Connected();
        var group = await rig.Chat.CreateGroupAsync("Load-shedding crew", [Them]);
        rig.Radio.CanSend = false;

        await rig.Chat.SendToGroupAsync(group.Id, "anyone there");

        Assert.Equal(ChatMessage.Pending, rig.Store.GetMessages(group.Id).Last().State);
    }
}

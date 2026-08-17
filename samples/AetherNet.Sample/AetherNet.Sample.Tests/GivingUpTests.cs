// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// When a message is allowed to be called failed.
///
/// <para>
/// "Failed" is a promise to the person that we have stopped trying. Showing it while the phone is
/// still working on the message is a lie in the direction that costs most — they retype it, or they
/// assume the other person never heard, and both are wrong.
/// </para>
///
/// <para>
/// The first message of a conversation is where this bit hardest. It goes out, the session under it
/// turns out to be broken, the repair starts — and the thirty-second receipt timer fires in the middle
/// of the recovery that is about to deliver it. Watched on hardware 2026-08-13: the opening line of
/// round eight showed failed while every message after it was confirmed both ways.
/// </para>
/// </summary>
public class GivingUpTests
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

        public Rig() => Chat = new ChatService(Store, new FakeIdentity(Me), Signal, PreKeys, Radio);

        public void Dispose() => Store.Dispose();
    }

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

    // ── An unconfirmed message is always tried again ──────────────────────────

    /// <summary>
    /// The store's own description of this list is "either they never went out, or they went out and
    /// were never confirmed" — a message sitting at <c>sent</c> is the second kind, and leaving it out
    /// means nothing ever picks it up again.
    /// </summary>
    [Fact]
    public void A_message_that_went_out_unconfirmed_is_still_owed()
    {
        using var store = AetherStore.InMemory();
        store.SaveMessage(new ChatMessage("id1", Them, "hello", Mine: true, ChatMessage.Sent, 1));

        Assert.Contains(store.GetUnsentMessages(Them), m => m.Id == "id1");
    }

    [Fact]
    public void A_message_the_peer_confirmed_is_not_owed()
    {
        using var store = AetherStore.InMemory();
        store.SaveMessage(new ChatMessage("id1", Them, "hello", Mine: true, ChatMessage.Delivered, 1));

        Assert.Empty(store.GetUnsentMessages(Them));
    }

    /// <summary>
    /// The two questions — "what do I owe this person" and "who do I owe anything to" — have to be
    /// answered by the same rule. A link coming up asks the second one to decide whose conversation to
    /// revive; if it uses a narrower rule, the phone with waiting messages is told it owes nobody, no
    /// session is ever started, and the backlog sits there behind a "setting up encryption…" that never
    /// finishes.
    /// </summary>
    [Theory]
    [InlineData(ChatMessage.Pending)]
    [InlineData(ChatMessage.Sent)]
    [InlineData(ChatMessage.Failed)]
    public void A_peer_who_is_owed_a_message_is_a_peer_worth_reviving(string state)
    {
        using var store = AetherStore.InMemory();
        store.SaveMessage(new ChatMessage("id1", Them, "hello", Mine: true, state, 1));

        Assert.Equal(
            store.GetUnsentMessages(Them).Count > 0,
            store.GetPeersWithUnsentMessages().Contains(Them));
    }

    [Fact]
    public void A_peer_with_nothing_outstanding_is_not_listed()
    {
        using var store = AetherStore.InMemory();
        store.SaveMessage(new ChatMessage("id1", Them, "hello", Mine: true, ChatMessage.Delivered, 1));

        Assert.Empty(store.GetPeersWithUnsentMessages());
    }

    [Fact]
    public async Task An_unconfirmed_message_goes_again_on_the_next_flush()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        rig.Store.SaveMessage(new ChatMessage("id1", Them, "hello", Mine: true, ChatMessage.Sent, 1));
        var before = rig.Radio.Sent.Count;

        await rig.Chat.FlushAsync(Them);

        Assert.True(rig.Radio.Sent.Count > before,
            "a message that was never confirmed sat there and was never tried again");
    }

    /// <summary>
    /// Now that everything unconfirmed is owed, a flush walks the whole backlog — so it must not
    /// re-send what is still in the air. Without this a phone with forty waiting messages turns every
    /// flush into forty more sends, and the timers those arm into forty flushes.
    /// </summary>
    [Fact]
    public async Task A_flush_does_not_send_a_message_that_is_still_in_flight()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "already on its way");
        var afterFirstSend = rig.Radio.Sent.Count;

        await rig.Chat.FlushAsync(Them);

        Assert.Equal(afterFirstSend, rig.Radio.Sent.Count);
    }

    [Fact]
    public async Task A_flush_does_send_a_message_that_is_no_longer_in_flight()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "gave up on this one");
        var id = rig.Store.GetMessages(Them).Single().Id;
        await rig.Chat.GiveUpIfUnconfirmedAsync(id, Them);      // no longer awaiting
        var afterGivingUp = rig.Radio.Sent.Count;

        await rig.Chat.FlushAsync(Them);

        Assert.True(rig.Radio.Sent.Count > afterGivingUp);
    }

    // ── Not while we are still working on it ──────────────────────────────────

    /// <summary>
    /// The case that broke the opening line of a conversation: the receipt timer runs out while the
    /// session it was sent over is being rebuilt. The message is not failed, it is mid-recovery.
    /// </summary>
    [Fact]
    public async Task A_message_is_not_failed_while_its_session_is_being_repaired()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "opening line");
        var id = rig.Store.GetMessages(Them).Single().Id;

        rig.Signal.DropSession(Them);          // as a repair does
        await rig.Chat.GiveUpIfUnconfirmedAsync(id, Them);

        Assert.NotEqual(ChatMessage.Failed, rig.Store.GetMessages(Them).Single().State);
    }

    [Fact]
    public async Task A_message_is_not_failed_while_there_is_no_link_to_send_it_on()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "opening line");
        var id = rig.Store.GetMessages(Them).Single().Id;

        rig.Radio.Unlink();
        await rig.Chat.GiveUpIfUnconfirmedAsync(id, Them);

        Assert.NotEqual(ChatMessage.Failed, rig.Store.GetMessages(Them).Single().State);
    }

    // ── But we do still give up eventually ────────────────────────────────────

    /// <summary>
    /// The other half of the promise. A good link, a live session, and still nothing back means the
    /// message really did not land, and saying so is the whole point of having the state at all.
    /// </summary>
    [Fact]
    public async Task A_message_is_failed_when_a_healthy_session_brings_nothing_back()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "into the void");
        var id = rig.Store.GetMessages(Them).Single().Id;

        await rig.Chat.GiveUpIfUnconfirmedAsync(id, Them);

        Assert.Equal(ChatMessage.Failed, rig.Store.GetMessages(Them).Single().State);
    }

    [Fact]
    public async Task A_message_the_peer_confirmed_is_never_failed()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "this one lands");
        var id = rig.Store.GetMessages(Them).Single().Id;
        rig.Store.SetMessageState(id, ChatMessage.Delivered);

        await rig.Chat.GiveUpIfUnconfirmedAsync(id, Them);

        Assert.Equal(ChatMessage.Delivered, rig.Store.GetMessages(Them).Single().State);
    }

    // ── The outcome that started this ─────────────────────────────────────────

    /// <summary>
    /// The opening line of a conversation, sent over a session that turns out to be broken, ends up
    /// confirmed like every other message rather than wearing a red mark for the rest of the thread.
    /// </summary>
    [Fact]
    public async Task The_first_message_of_a_conversation_survives_a_repair_under_it()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();
        await rig.Chat.SendAsync(Them, "opening line");
        var id = rig.Store.GetMessages(Them).Single().Id;

        rig.Signal.DropSession(Them);                       // the session under it dies
        await rig.Chat.GiveUpIfUnconfirmedAsync(id, Them);   // the timer fires mid-repair
        rig.PreKeys.RaiseBundleReceived(Them);               // recovery completes

        Assert.True(await Eventually(() =>
            rig.Store.GetMessages(Them).Single().State != ChatMessage.Failed),
            "the opening line is still showing as failed after the conversation recovered");
    }
}

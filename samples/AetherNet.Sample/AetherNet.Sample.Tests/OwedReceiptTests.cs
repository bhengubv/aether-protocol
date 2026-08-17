// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Receipts this phone owes someone and could not send yet.
///
/// <para>
/// A receipt can only go inside a secure session, so there are moments when a message arrives, is read,
/// is saved, and cannot be confirmed: the session was just thrown away and is being rebuilt, or the
/// link dropped between the message landing and the answer going out. Doing nothing in that moment
/// loses the receipt for good — the message is on this phone, the person who sent it is told it failed,
/// and nothing will ever correct that because the message is not going to arrive a second time.
/// </para>
///
/// <para>
/// Watched on hardware 2026-08-13: the P30 received merlin's messages during a session repair, filed
/// every one of them, and sent no receipts. Merlin showed them all as failures while the P30 sat there
/// reading them.
/// </para>
/// </summary>
public class OwedReceiptTests
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

    /// <summary>Two phones on one radio, so the receipts under test are real ones.</summary>
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

    // ── The store remembers what is owed ──────────────────────────────────────

    [Fact]
    public void A_receipt_that_could_not_be_sent_is_remembered()
    {
        using var store = AetherStore.InMemory();

        store.RememberOwedReceipt(Them, "abc123");

        Assert.Contains("abc123", store.GetOwedReceipts(Them));
    }

    [Fact]
    public void A_receipt_that_went_out_is_no_longer_owed()
    {
        using var store = AetherStore.InMemory();
        store.RememberOwedReceipt(Them, "abc123");

        store.ForgetOwedReceipt("abc123");

        Assert.Empty(store.GetOwedReceipts(Them));
    }

    [Fact]
    public void Owing_the_same_receipt_twice_is_owing_it_once()
    {
        using var store = AetherStore.InMemory();

        store.RememberOwedReceipt(Them, "abc123");
        store.RememberOwedReceipt(Them, "abc123");

        Assert.Single(store.GetOwedReceipts(Them));
    }

    [Fact]
    public void Receipts_owed_to_one_person_are_not_owed_to_another()
    {
        using var store = AetherStore.InMemory();

        store.RememberOwedReceipt(Them, "abc123");

        Assert.Empty(store.GetOwedReceipts("ZZZZZ-ZZZZZ"));
    }

    // ── The chat layer owes, and then pays ────────────────────────────────────

    /// <summary>
    /// The message crosses, is read, is saved — and the link drops before the answer can go back. The
    /// sender is now waiting on a receipt for a message that has already arrived.
    /// </summary>
    [Fact]
    public async Task A_receipt_that_could_not_go_out_is_owed()
    {
        using var pair = new Pair();
        pair.A.Radio.CanSend = false;          // A can still hear; it just cannot answer

        await pair.ChatB.SendAsync(Me, "did you get this");

        Assert.True(await Eventually(() => pair.A.Store.GetOwedReceipts(Them).Count == 1),
            "the receipt could not be sent and was simply forgotten");
    }

    [Fact]
    public async Task An_owed_receipt_goes_out_when_the_session_comes_back()
    {
        using var pair = new Pair();
        pair.A.Radio.CanSend = false;
        await pair.ChatB.SendAsync(Me, "did you get this");
        await Eventually(() => pair.A.Store.GetOwedReceipts(Them).Count == 1);

        pair.A.Radio.CanSend = true;
        pair.A.Signal.DropSession(Them);
        var before = pair.A.Radio.Sent.Count;
        pair.A.PreKeys.RaiseBundleReceived(Them);   // a session again

        Assert.True(await Eventually(() => pair.A.Radio.Sent.Count > before),
            "a session came up and the receipt this phone owed never went");
    }

    [Fact]
    public async Task A_receipt_that_goes_out_stops_being_owed()
    {
        using var pair = new Pair();
        pair.A.Radio.CanSend = false;
        await pair.ChatB.SendAsync(Me, "did you get this");
        await Eventually(() => pair.A.Store.GetOwedReceipts(Them).Count == 1);

        pair.A.Radio.CanSend = true;
        pair.A.Signal.DropSession(Them);
        pair.A.PreKeys.RaiseBundleReceived(Them);

        Assert.True(await Eventually(() => pair.A.Store.GetOwedReceipts(Them).Count == 0));
    }

    /// <summary>
    /// The outcome the person actually sees: a message that showed as failed goes back to confirmed
    /// once the receipt finally makes it across.
    /// </summary>
    [Fact]
    public async Task A_late_receipt_clears_the_failure_on_the_other_phone()
    {
        using var pair = new Pair();
        pair.A.Radio.CanSend = false;
        await pair.ChatB.SendAsync(Me, "did you get this");
        await Eventually(() => pair.A.Store.GetOwedReceipts(Them).Count == 1);

        var sent = pair.StoreB.GetMessages(Me).Single();
        pair.StoreB.SetMessageState(sent.Id, ChatMessage.Failed);      // B gave up waiting

        pair.A.Radio.CanSend = true;
        pair.A.Signal.DropSession(Them);
        pair.A.PreKeys.RaiseBundleReceived(Them);

        Assert.True(await Eventually(() =>
            pair.StoreB.GetMessages(Me).Single().State == ChatMessage.Delivered),
            "the receipt arrived and the message is still showing as failed");
    }

    // ── Nothing is owed when nothing went wrong ───────────────────────────────

    [Fact]
    public async Task A_receipt_that_went_straight_out_is_never_owed()
    {
        using var pair = new Pair();

        await pair.ChatB.SendAsync(Me, "did you get this");
        await Eventually(() => pair.StoreB.GetMessages(Me).Single().State == ChatMessage.Delivered);

        Assert.Empty(pair.A.Store.GetOwedReceipts(Them));
    }
}

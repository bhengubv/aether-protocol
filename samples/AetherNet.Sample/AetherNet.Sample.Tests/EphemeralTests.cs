// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Disappearing messages: the wire header that carries the rule, and the receiver-side enforcement that
/// counts views, runs the timeout clock, and burns the message when it is spent.
/// </summary>
public class EphemeralTests
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

        public Rig() => Chat = ConvergedChat.Build(Store, new FakeIdentity(Me), Signal, PreKeys, Radio);

        public void Dispose() => Store.Dispose();
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static ChatMessage Received(string id, int kind, long windowMs = 0, string body = "secret") => new(
        Id: id, PeerTag: Them, Body: body, Mine: false, State: ChatMessage.Received, SentMs: 1,
        EphemeralKind: kind, EphemeralWindowMs: windowMs);

    // ── The wire header ────────────────────────────────────────────────────────

    [Fact]
    public void The_rule_survives_a_round_trip()
    {
        var encoded = new EphemeralRef(ChatMessage.EphTimeout, 120_000).Encode("hello");
        var (eph, rest) = EphemeralRef.Decode(encoded);

        Assert.NotNull(eph);
        Assert.Equal(ChatMessage.EphTimeout, eph!.Kind);
        Assert.Equal(120_000, eph.WindowMs);
        Assert.Equal("hello", rest);
    }

    [Fact]
    public void It_wraps_an_attachment_header_without_disturbing_it()
    {
        // The composed body a note carries: ephemeral header, then attachment header, then caption.
        var body = new EphemeralRef(ChatMessage.EphOnce, 0)
            .Encode(new AttachmentRef("HASH", "image/jpeg", 99).Encode("a caption"));

        var (eph, afterEph) = EphemeralRef.Decode(body);
        var (att, caption) = AttachmentRef.Decode(afterEph);

        Assert.Equal(ChatMessage.EphOnce, eph!.Kind);
        Assert.Equal("HASH", att!.Hash);
        Assert.Equal("image/jpeg", att.ContentType);
        Assert.Equal("a caption", caption);
    }

    [Fact]
    public void Plain_text_is_not_ephemeral()
    {
        var (eph, rest) = EphemeralRef.Decode("just an ordinary message");
        Assert.Null(eph);
        Assert.Equal("just an ordinary message", rest);
    }

    [Fact]
    public void A_timeout_window_is_clamped_to_five_minutes_on_the_way_in()
    {
        var (eph, _) = EphemeralRef.Decode(new EphemeralRef(ChatMessage.EphTimeout, 99_999_999).Encode(""));
        Assert.Equal(ChatMessage.EphemeralMaxWindowMs, eph!.WindowMs);
    }

    // ── Receiver-side enforcement ──────────────────────────────────────────────

    [Fact]
    public async Task View_once_burns_after_a_single_view()
    {
        using var rig = new Rig();
        rig.Store.SaveMessage(Received("e1", ChatMessage.EphOnce));

        Assert.True(rig.Chat.OpenEphemeral("e1"));      // shown this once
        await rig.Chat.BurnIfSpentAsync("e1");          // closing it spends it

        var after = rig.Store.GetMessage("e1");
        Assert.True(after!.EphemeralSpent);
        Assert.Equal("", after.Body);                    // the words are gone; a tombstone remains
    }

    [Fact]
    public async Task View_twice_survives_the_first_view_and_burns_after_the_second()
    {
        using var rig = new Rig();
        rig.Store.SaveMessage(Received("e2", ChatMessage.EphTwice, body: "twice"));

        Assert.True(rig.Chat.OpenEphemeral("e2"));
        await rig.Chat.BurnIfSpentAsync("e2");
        Assert.False(rig.Store.GetMessage("e2")!.EphemeralSpent);   // one view left
        Assert.Equal("twice", rig.Store.GetMessage("e2")!.Body);

        Assert.True(rig.Chat.OpenEphemeral("e2"));
        await rig.Chat.BurnIfSpentAsync("e2");
        Assert.True(rig.Store.GetMessage("e2")!.EphemeralSpent);    // now spent
    }

    [Fact]
    public async Task A_timeout_burns_once_its_window_has_passed_but_not_before()
    {
        using var rig = new Rig();

        // Opened a while ago, window already elapsed → spent.
        rig.Store.SaveMessage(Received("t1", ChatMessage.EphTimeout, windowMs: 60_000));
        rig.Store.UpdateEphemeral("t1", views: 1, startedMs: Now - 70_000, spent: false);
        await rig.Chat.BurnIfSpentAsync("t1");
        Assert.True(rig.Store.GetMessage("t1")!.EphemeralSpent);

        // Opened just now, window still open → survives.
        rig.Store.SaveMessage(Received("t2", ChatMessage.EphTimeout, windowMs: 60_000, body: "still here"));
        rig.Store.UpdateEphemeral("t2", views: 1, startedMs: Now, spent: false);
        await rig.Chat.BurnIfSpentAsync("t2");
        Assert.False(rig.Store.GetMessage("t2")!.EphemeralSpent);
        Assert.Equal("still here", rig.Store.GetMessage("t2")!.Body);
    }

    [Fact]
    public async Task The_senders_own_copy_is_never_enforced()
    {
        using var rig = new Rig();
        // Mine = true: this phone is the sender; its copy stays put.
        rig.Store.SaveMessage(new ChatMessage(
            Id: "mine1", PeerTag: Them, Body: "for your eyes only", Mine: true,
            State: ChatMessage.Sent, SentMs: 1, EphemeralKind: ChatMessage.EphOnce));

        Assert.False(rig.Chat.OpenEphemeral("mine1"));   // nothing to enforce on our own message
        await rig.Chat.BurnIfSpentAsync("mine1");

        var after = rig.Store.GetMessage("mine1");
        Assert.False(after!.EphemeralSpent);
        Assert.Equal("for your eyes only", after.Body);
    }

    [Fact]
    public void A_received_ephemeral_message_is_persisted_with_its_rule()
    {
        using var rig = new Rig();
        rig.Store.SaveMessage(Received("p1", ChatMessage.EphTimeout, windowMs: 90_000));

        var back = rig.Store.GetMessage("p1");
        Assert.True(back!.IsEphemeral);
        Assert.Equal(ChatMessage.EphTimeout, back.EphemeralKind);
        Assert.Equal(90_000, back.EphemeralWindowMs);
    }
}

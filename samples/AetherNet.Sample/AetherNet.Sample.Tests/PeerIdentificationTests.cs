// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Working out who is on the other end of a link.
///
/// <para>
/// A radio never learns this by itself. The long-term identity deliberately does not travel in clear —
/// the handshake carries a rotating wire address — so the radio can only report that <b>someone</b> is
/// there. Found on device 2026-08-17: both phones were delivering messages to exactly the right person
/// while their chat screens said <c>"connected to someone else"</c>, because the screen compared the
/// contact's AetherTag against a wire address and drew the obvious conclusion.
/// </para>
///
/// <para>
/// The identity arrives inside the session, so the layer that opens the session supplies it — and only
/// once ciphertext has actually opened. A packet header is a claim anyone can write; ciphertext that
/// opens under a peer's ratchet could only have come from them.
/// </para>
/// </summary>
public class PeerIdentificationTests
{
    /// <summary>What a radio actually reports after a handshake: 16 base-32 characters, not a tag.</summary>
    private const string Wire = "N15XN2VSGGV0SAMC";
    private const string TheirWire = "NR3FJ9EAEPVB3ZF7";

    private const string Tag = "KSQMM-T9G3E";
    private const string MyTag = "QAVYZ-K8YFY";

    private static FakeRadioMesh ALinkedRadio()
    {
        var radio = new FakeRadioMesh("QAVYZ-K8YFY") { PeerLabel = Wire };
        radio.Link();
        return radio;
    }

    // ── Before anyone is identified ───────────────────────────────────────────

    [Fact]
    public void A_fresh_link_is_named_by_its_wire_address()
    {
        Assert.Equal(Wire, ALinkedRadio().PeerTag);
    }

    [Fact]
    public void An_unlinked_radio_has_no_peer()
    {
        Assert.Null(new FakeRadioMesh("QAVYZ-K8YFY").PeerTag);
    }

    // ── Once they are ─────────────────────────────────────────────────────────

    [Fact]
    public void An_identified_peer_is_named_by_their_tag()
    {
        var radio = ALinkedRadio();

        radio.IdentifyPeer(Tag);

        Assert.Equal(Tag, radio.PeerTag);
    }

    /// <summary>The screen re-renders off <c>Changed</c>; without it the header keeps the old answer.</summary>
    [Fact]
    public void Identifying_a_peer_tells_the_ui_to_look_again()
    {
        var radio = ALinkedRadio();
        var told = 0;
        radio.Changed += () => told++;

        radio.IdentifyPeer(Tag);

        Assert.True(told > 0, "the UI was never told the peer had a name");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_is_not_an_identification(string? nothing)
    {
        var radio = ALinkedRadio();

        radio.IdentifyPeer(nothing!);

        Assert.Equal(Wire, radio.PeerTag);
    }

    [Fact]
    public void Identifying_the_same_peer_twice_is_not_news()
    {
        var radio = ALinkedRadio();
        radio.IdentifyPeer(Tag);

        radio.IdentifyPeer(Tag);

        Assert.Single(radio.Identified);
    }

    // ── A link going away ─────────────────────────────────────────────────────

    /// <summary>
    /// The address rotates, so the next link is a different stranger until proven otherwise. Carrying
    /// the last identification over would let the screen name someone who is no longer there.
    /// </summary>
    [Fact]
    public void A_new_link_starts_out_anonymous_again()
    {
        var radio = ALinkedRadio();
        radio.IdentifyPeer(Tag);

        radio.Unlink();
        radio.Link();

        Assert.Equal(Wire, radio.PeerTag);
    }

    [Fact]
    public void An_unlinked_radio_forgets_who_was_there()
    {
        var radio = ALinkedRadio();
        radio.IdentifyPeer(Tag);

        radio.Unlink();

        Assert.Null(radio.PeerTag);
    }

    // ── The chat layer is what knows ──────────────────────────────────────────

    /// <summary>
    /// Two phones whose radios name each other by wire address, exactly as the hardware does. The
    /// radio cannot identify anyone by itself, so this only passes if the layer that opens the session
    /// tells it — and only once something has actually opened.
    /// </summary>
    private sealed class Pair : IDisposable
    {
        public AetherStore StoreA { get; } = AetherStore.InMemory();
        public AetherStore StoreB { get; } = AetherStore.InMemory();
        public FakeSignalProtocol SignalA { get; } = new();
        public FakeSignalProtocol SignalB { get; } = new();
        public FakeRadioMesh RadioA { get; } = new(MyTag) { PeerLabel = Wire };
        public FakeRadioMesh RadioB { get; } = new(Tag) { PeerLabel = TheirWire };
        public ChatService ChatA { get; }
        public ChatService ChatB { get; }

        public Pair()
        {
            ChatA = new ChatService(StoreA, new FakeIdentity(MyTag), SignalA, new FakePreKeyExchange(), RadioA);
            ChatB = new ChatService(StoreB, new FakeIdentity(Tag), SignalB, new FakePreKeyExchange(), RadioB);

            RadioA.Peer = RadioB;
            RadioB.Peer = RadioA;
            SignalA.OpenSessionWith(Tag);
            SignalB.OpenSessionWith(MyTag);
            RadioA.Link();
            RadioB.Link();
        }

        public void Dispose() { StoreA.Dispose(); StoreB.Dispose(); }
    }

    private static async Task<bool> Eventually(Func<bool> condition, int withinMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(withinMs);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(10);
        return condition();
    }

    [Fact]
    public async Task A_message_that_opens_names_the_person_who_sent_it()
    {
        using var pair = new Pair();
        Assert.Equal(Wire, pair.RadioA.PeerTag);          // both anonymous to begin with

        await pair.ChatA.SendAsync(Tag, "hello");

        Assert.True(await Eventually(() => pair.RadioB.PeerTag == MyTag),
            $"the receiving phone still calls the link '{pair.RadioB.PeerTag}'");
    }

    /// <summary>
    /// And back the other way, off the receipt alone — which matters, because the person you spoke to
    /// first usually acknowledges before they say anything of their own.
    /// </summary>
    [Fact]
    public async Task A_receipt_coming_back_names_the_person_who_sent_it()
    {
        using var pair = new Pair();

        await pair.ChatA.SendAsync(Tag, "hello");

        Assert.True(await Eventually(() => pair.RadioA.PeerTag == Tag),
            $"the sending phone still calls the link '{pair.RadioA.PeerTag}'");
    }

    /// <summary>
    /// The header claim is not the evidence — the decryption is. A payload that says who it is from but
    /// will not open under their ratchet must leave the link anonymous, or anyone within earshot could
    /// rename it by writing a tag into a header.
    /// </summary>
    [Fact]
    public async Task A_payload_that_will_not_open_names_nobody()
    {
        using var pair = new Pair();
        pair.SignalB.RatchetBroken = true;

        await pair.ChatA.SendAsync(Tag, "hello");

        Assert.False(await Eventually(() => pair.RadioB.PeerTag == MyTag, 400),
            "an unreadable payload was allowed to name the link");
        Assert.Equal(TheirWire, pair.RadioB.PeerTag);
    }
}

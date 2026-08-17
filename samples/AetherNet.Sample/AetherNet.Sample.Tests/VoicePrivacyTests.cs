// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A call is at least as private as a message.
///
/// <para>
/// The voice service shipped its signalling as plain JSON and its frames as raw bytes, while chat had
/// ridden the Signal ratchet since the mock was removed. Anyone within radio range could have
/// reconstructed who rang whom, when, and for how long — and the offer helpfully names the codec, so
/// the frames after it were readable too.
/// </para>
///
/// <para>
/// These are the tests that stop that coming back. The rule is not "voice is usually encrypted", it is
/// <b>voice does not go out at all unless it can be sealed</b>.
/// </para>
/// </summary>
public class VoicePrivacyTests
{
    private const string Me = "QAVYZ-K8YFY";
    private const string Them = "KSQMM-T9G3E";

    /// <summary>Records what was actually handed to the radio, which is the only thing that matters here.</summary>
    private sealed class SpySender : AetherNet.Routing.IMeshSender
    {
        public List<MeshPacket> Sent { get; } = [];

        public string LocalUhid => Me;
        public string? LocalGeohash => null;
        public IReadOnlyList<AetherNet.Models.PeerInfo> GetConnectedPeers() => [];

        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        {
            Sent.Add(packet);
            return Task.FromResult(true);
        }

        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        {
            Sent.Add(packet);
            return Task.FromResult(1);
        }
    }

    private static MeshPacket AnOfferTo(string peer) => new()
    {
        Type = PacketType.VoiceSignaling,
        SourceUhid = Me,
        DestinationUhid = peer,
        Ttl = 7,
        Payload = Encoding.UTF8.GetBytes("""{"kind":"offer","from_uhid":"QAVYZ-K8YFY","codecs":["opus"]}"""),
    };

    // ── Nothing readable leaves the phone ─────────────────────────────────────

    /// <summary>
    /// Every byte of signalling goes through the peer's ratchet on the way out.
    ///
    /// <para>
    /// Asserted as "it was sealed, for the right person" rather than "the output looks scrambled" —
    /// the test double passes plaintext through, so checking the bytes would only be testing the
    /// envelope's encoding and would keep passing if the encryption were removed entirely.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Signalling_is_sealed_for_the_peer_before_it_goes_out()
    {
        var spy = new SpySender();
        var signal = new FakeSignalProtocol();
        signal.OpenSessionWith(Them);
        var offer = AnOfferTo(Them);

        await new EncryptedMeshSender(spy, signal).SendAsync(offer, Them);

        var (peer, plaintext) = Assert.Single(signal.Encrypted);
        Assert.Equal(Them, peer);
        Assert.Equal(offer.Payload, plaintext);
    }

    /// <summary>The bytes handed to the radio are the sealed envelope, not the payload we were given.</summary>
    [Fact]
    public async Task What_reaches_the_radio_is_not_what_was_handed_in()
    {
        var spy = new SpySender();
        var signal = new FakeSignalProtocol();
        signal.OpenSessionWith(Them);
        var offer = AnOfferTo(Them);

        await new EncryptedMeshSender(spy, signal).SendAsync(offer, Them);

        Assert.NotEqual(offer.Payload, spy.Sent.Single().Payload);
    }

    /// <summary>
    /// The header still has to say where it is going — a radio cannot deliver a packet otherwise — but
    /// nothing about the call itself may be legible.
    /// </summary>
    [Fact]
    public async Task The_sealed_packet_keeps_its_type_and_destination()
    {
        var spy = new SpySender();
        var signal = new FakeSignalProtocol();
        signal.OpenSessionWith(Them);

        await new EncryptedMeshSender(spy, signal).SendAsync(AnOfferTo(Them), Them);

        var sent = spy.Sent.Single();
        Assert.Equal(PacketType.VoiceSignaling, sent.Type);
        Assert.Equal(Them, sent.DestinationUhid);
    }

    // ── It fails closed ───────────────────────────────────────────────────────

    /// <summary>
    /// The property that matters most. With no session there is no way to seal the packet, and the
    /// only two options are "send it in clear" and "do not send it". A call that does not connect is a
    /// far smaller problem than one that quietly broadcasts.
    /// </summary>
    [Fact]
    public async Task Nothing_is_sent_at_all_when_there_is_no_session()
    {
        var spy = new SpySender();
        var sender = new EncryptedMeshSender(spy, new FakeSignalProtocol());   // no session with anyone

        var ok = await sender.SendAsync(AnOfferTo(Them), Them);

        Assert.False(ok);
        Assert.Empty(spy.Sent);
    }

    [Fact]
    public async Task Nothing_is_broadcast_in_clear_when_there_is_no_session()
    {
        var spy = new SpySender();
        var sender = new EncryptedMeshSender(spy, new FakeSignalProtocol());

        var reached = await sender.BroadcastAsync(AnOfferTo(Them));

        Assert.Equal(0, reached);
        Assert.Empty(spy.Sent);
    }

    /// <summary>
    /// A packet with nobody to seal it to cannot be sealed. Broadcasting it in clear would be exactly
    /// the leak this class exists to prevent, so it is dropped.
    /// </summary>
    [Fact]
    public async Task A_packet_with_no_destination_is_dropped_rather_than_broadcast()
    {
        var spy = new SpySender();
        var signal = new FakeSignalProtocol();
        signal.OpenSessionWith(Them);

        var ok = await new EncryptedMeshSender(spy, signal)
            .SendAsync(AnOfferTo(peer: ""), "");

        Assert.False(ok);
        Assert.Empty(spy.Sent);
    }

    [Fact]
    public async Task A_ratchet_that_will_not_encrypt_stops_the_packet()
    {
        var spy = new SpySender();
        var signal = new FakeSignalProtocol { EncryptFails = true };
        signal.OpenSessionWith(Them);

        var ok = await new EncryptedMeshSender(spy, signal).SendAsync(AnOfferTo(Them), Them);

        Assert.False(ok);
        Assert.Empty(spy.Sent);
    }

    // ── And it comes back ─────────────────────────────────────────────────────

    [Fact]
    public async Task What_was_sealed_opens_again_at_the_other_end()
    {
        var spy = new SpySender();
        var signal = new FakeSignalProtocol();
        signal.OpenSessionWith(Them);
        await new EncryptedMeshSender(spy, signal).SendAsync(AnOfferTo(Them), Them);

        var opened = await EncryptedMeshSender.UnsealAsync(spy.Sent.Single(), signal, Them);

        Assert.NotNull(opened);
        Assert.Contains("offer", Encoding.UTF8.GetString(opened!.Payload!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A packet claiming to be from someone will not open under their ratchet unless it really was.
    /// Returning null rather than throwing keeps a hostile packet from being a way to crash a call.
    /// </summary>
    [Fact]
    public async Task A_payload_that_will_not_open_is_dropped_rather_than_thrown()
    {
        var signal = new FakeSignalProtocol();
        signal.OpenSessionWith(Them);
        var rubbish = new MeshPacket
        {
            Type = PacketType.VoiceCall,
            SourceUhid = Them,
            DestinationUhid = Me,
            Payload = [1, 2, 3, 4, 5],
        };

        var opened = await EncryptedMeshSender.UnsealAsync(rubbish, signal, Them);

        Assert.Null(opened);
    }
}

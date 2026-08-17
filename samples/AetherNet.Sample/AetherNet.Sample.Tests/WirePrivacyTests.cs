// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Identity;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The wire-privacy rules from <c>IDENTITY_AND_DATA_SOVEREIGNTY</c> §1 and
/// <c>PRIVACY_THREAT_MODEL</c> #1 CRITICAL.
///
/// <para>
/// "The long-term identity (Ed25519 + AetherTag) is <b>never on the wire in clear</b> — only revealed
/// inside an established Signal session; the wire uses rotating ERIDs."
/// </para>
///
/// <para>
/// The harm is not abstract. A permanent identity broadcast in the open lets anyone within radio
/// range enumerate and follow every phone in a room, forever, without ever connecting to one — and
/// lets a hostile network pick out exactly which nodes to cut off.
/// </para>
/// </summary>
public class WirePrivacyTests
{
    private const string Tag = "KXJB7-MN2P4";

    private static byte[] ARoutingKey()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        return key;
    }

    // ── The identity stays off the wire ───────────────────────────────────────

    [Fact]
    public void A_handshake_frame_does_not_carry_the_tag_in_clear()
    {
        var frame = MeshFraming.Handshake(ARoutingKey());

        Assert.DoesNotContain(Tag, Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public void Nothing_sent_before_a_session_identifies_the_sender()
    {
        var routingKey = ARoutingKey();

        // The opener, plus anything the transport fragments before any key agreement.
        var frames = new List<byte[]> { MeshFraming.Handshake(routingKey) };
        frames.AddRange(MeshFraming.Fragment(Encoding.UTF8.GetBytes("hello"), mtu: 185, messageId: 1));

        foreach (var frame in frames)
            Assert.DoesNotContain(Tag, Encoding.UTF8.GetString(frame));
    }

    /// <summary>
    /// The rule is enforced by the builder, not by everyone remembering it. A caller that hands over
    /// a long-term tag is refused rather than quietly obliged.
    /// </summary>
    [Fact]
    public void The_frame_builder_refuses_to_carry_a_long_term_tag() =>
        Assert.Throws<ArgumentException>(() => MeshFraming.HandshakeFor(Tag));

    // ── The address rotates ───────────────────────────────────────────────────

    [Fact]
    public void The_address_changes_between_epochs()
    {
        var routingKey = ARoutingKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var early = MeshFraming.ReadHandshake(MeshFraming.Handshake(routingKey, now));
        var later = MeshFraming.ReadHandshake(MeshFraming.Handshake(routingKey, now.AddHours(1)));

        Assert.NotEqual(early, later);
    }

    [Fact]
    public void The_address_is_stable_within_an_epoch()
    {
        var routingKey = ARoutingKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var first = MeshFraming.ReadHandshake(MeshFraming.Handshake(routingKey, now));
        var second = MeshFraming.ReadHandshake(MeshFraming.Handshake(routingKey, now.AddSeconds(1)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Two_phones_do_not_share_an_address()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var a = MeshFraming.ReadHandshake(MeshFraming.Handshake(ARoutingKey(), now));
        var b = MeshFraming.ReadHandshake(MeshFraming.Handshake(ARoutingKey(), now));

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// The routing key is derived from the identity secret, not equal to it — so an address on the
    /// wire never walks back to the key that signs.
    /// </summary>
    [Fact]
    public void The_address_is_not_the_routing_key()
    {
        var routingKey = ARoutingKey();

        var address = MeshFraming.ReadHandshake(MeshFraming.Handshake(routingKey));

        Assert.NotEqual(Convert.ToBase64String(routingKey), address);
    }

    /// <summary>
    /// A rotating address is only unlinkable if it is rotated with something an observer cannot
    /// have. Derive it from the AetherTag — which is public, printed on QR codes and read aloud —
    /// and anyone can compute every address that tag will ever use, which is worse than sending the
    /// tag itself: it looks private while offering nothing.
    /// </summary>
    [Fact]
    public void An_address_cannot_be_computed_from_public_information()
    {
        var fromPublicTag = EphemeralRoutingId.DeriveRoutingKey(Encoding.UTF8.GetBytes(Tag));
        var fromSecret = EphemeralRoutingId.DeriveRoutingKey(ARoutingKey());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var guessable = MeshFraming.ReadHandshake(MeshFraming.Handshake(fromPublicTag, now));
        var actual = MeshFraming.ReadHandshake(MeshFraming.Handshake(fromSecret, now));

        Assert.NotEqual(guessable, actual);
    }

    // ── Nothing names the ecosystem ───────────────────────────────────────────

    /// <summary>
    /// <c>CIRCLEAETHER_PARITY_PLAN</c> P2.2b: a node must be byte-indistinguishable from any other
    /// AetherNet node. "A network whose members are individually flaggable isn't private."
    /// </summary>
    [Theory]
    [InlineData("circle")]
    [InlineData("geek")]
    [InlineData("sdpkt")]
    [InlineData("tgn")]
    [InlineData("bhengu")]
    public void No_frame_names_the_ecosystem_that_produced_it(string brand)
    {
        var frames = new List<byte[]> { MeshFraming.Handshake(ARoutingKey()) };
        frames.AddRange(MeshFraming.Fragment(Encoding.UTF8.GetBytes("hello"), mtu: 185, messageId: 1));

        foreach (var frame in frames)
            Assert.DoesNotContain(brand, Encoding.UTF8.GetString(frame), StringComparison.OrdinalIgnoreCase);
    }
}

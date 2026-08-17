// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The last place the long-term identity was still in the open.
///
/// <para>
/// The handshake and the service record now carry a rotating address, but a <c>MeshPacket</c> header
/// is cleartext by design — <c>SourceUhid</c> and <c>DestinationUhid</c> are readable before any
/// decryption. Stamping the AetherTag there put it back on the air on every single packet, which is
/// exactly the threat model's #1 CRITICAL finding: "a stable, PII-derived SourceUhid in cleartext on
/// every packet … persistent cross-context tracking."
/// </para>
///
/// <para>
/// So the header carries the same rotating address the radios use, and the identity is exchanged
/// where it belongs — inside the encrypted session.
/// </para>
/// </summary>
public class PacketAddressingTests
{
    private const string Tag = "KXJB7-MN2P4";

    private static byte[] ARoutingKey()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        return key;
    }

    // ── Addresses, not identities ─────────────────────────────────────────────

    [Fact]
    public void A_wire_address_is_not_an_aether_tag()
    {
        var address = WireAddress.For(ARoutingKey());

        Assert.False(AetherNetTag.TryParse(address, out _),
            "the wire address parses as a long-term tag — it is the identity, not a stand-in for it");
    }

    [Fact]
    public void A_wire_address_rotates_between_epochs()
    {
        var key = ARoutingKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.NotEqual(WireAddress.For(key, now), WireAddress.For(key, now.AddHours(1)));
    }

    [Fact]
    public void A_wire_address_holds_still_within_an_epoch()
    {
        var key = ARoutingKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.Equal(WireAddress.For(key, now), WireAddress.For(key, now.AddSeconds(1)));
    }

    [Fact]
    public void Two_phones_never_share_a_wire_address()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.NotEqual(WireAddress.For(ARoutingKey(), now), WireAddress.For(ARoutingKey(), now));
    }

    [Fact]
    public void A_wire_address_cannot_be_computed_from_the_public_tag()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var fromTag = EphemeralRoutingId.DeriveRoutingKey(System.Text.Encoding.UTF8.GetBytes(Tag));

        Assert.NotEqual(WireAddress.For(fromTag, now), WireAddress.For(ARoutingKey(), now));
    }

    // ── Recognising yourself ──────────────────────────────────────────────────

    [Fact]
    public void A_phone_recognises_its_own_current_address()
    {
        var key = ARoutingKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.True(WireAddress.IsMine(WireAddress.For(key, now), key, now));
    }

    /// <summary>
    /// A packet sent just before an epoch boundary arrives just after it. Refusing it would drop
    /// real traffic every fifteen minutes, so the previous epoch still counts as ours.
    /// </summary>
    [Fact]
    public void A_phone_still_recognises_the_address_it_used_last_epoch()
    {
        var key = ARoutingKey();

        // A minute before an epoch turns over, and a minute after — the case that happens constantly
        // in real traffic and must not drop a packet.
        var boundary = EphemeralRoutingId.EpochFor(1_700_000_000) * 900;
        var beforeTurn = DateTimeOffset.FromUnixTimeSeconds(boundary - 60);
        var afterTurn = DateTimeOffset.FromUnixTimeSeconds(boundary + 60);

        Assert.True(WireAddress.IsMine(WireAddress.For(key, beforeTurn), key, afterTurn));
    }

    [Fact]
    public void A_phone_does_not_claim_someone_elses_address()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.False(WireAddress.IsMine(WireAddress.For(ARoutingKey(), now), ARoutingKey(), now));
    }

    [Fact]
    public void A_phone_does_not_claim_an_address_from_long_ago()
    {
        var key = ARoutingKey();
        var longAgo = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.False(WireAddress.IsMine(WireAddress.For(key, longAgo), key, longAgo.AddDays(1)));
    }
}

// SPDX-License-Identifier: MIT

using AetherNet.Rendezvous;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Two phones arriving at the same group without saying a word to each other.
///
/// <para>
/// This replaces every attempt to discover or exchange, both of which failed for the same underlying
/// reason: they tried to learn something both phones already knew. So the property that matters most
/// here is agreement — if the two ends ever derive different credentials, the host sits in a group
/// nobody can join and nothing in the app will say why.
/// </para>
/// </summary>
public class GroupCredentialsTests
{
    // ── agreement ──────────────────────────────────────────────────────────

    /// <summary>
    /// The whole mechanism in one assertion: the same host key gives the same group, every time, on
    /// every phone. Nothing is random, nothing is negotiated, nothing is sent.
    /// </summary>
    [Fact]
    public void The_same_host_always_gives_the_same_group()
    {
        var host = new FakeIdentity();

        var onePhone = GroupCredentials.ForHost(host.PublicKey);
        var another = GroupCredentials.ForHost(host.PublicKey);

        Assert.NotNull(onePhone);
        Assert.Equal(onePhone!.NetworkName, another!.NetworkName);
        Assert.Equal(onePhone.Passphrase, another.Passphrase);
    }

    /// <summary>
    /// Two Circles must not collide. Different hosts, different groups — or a phone would wander into
    /// a group belonging to people it has never met.
    /// </summary>
    [Fact]
    public void A_different_host_gives_a_different_group()
    {
        var one = GroupCredentials.ForHost(new FakeIdentity().PublicKey);
        var other = GroupCredentials.ForHost(new FakeIdentity().PublicKey);

        Assert.NotEqual(one!.NetworkName, other!.NetworkName);
        Assert.NotEqual(one.Passphrase, other.Passphrase);
    }

    /// <summary>The name and the passphrase must not be the same value wearing two hats.</summary>
    [Fact]
    public void The_name_does_not_give_away_the_passphrase()
    {
        var credentials = GroupCredentials.ForHost(new FakeIdentity().PublicKey);

        Assert.DoesNotContain(credentials!.Passphrase, credentials.NetworkName, StringComparison.Ordinal);
    }

    // ── what Android will accept ───────────────────────────────────────────

    /// <summary>
    /// Android rejects a network name that does not start with DIRECT-, and rejects a passphrase
    /// outside 8-63 characters. Getting either wrong fails at createGroup with a bare reason code, so
    /// it is worth failing here instead.
    /// </summary>
    [Fact]
    public void Android_will_accept_what_this_produces()
    {
        var credentials = GroupCredentials.ForHost(new FakeIdentity().PublicKey);

        Assert.StartsWith("DIRECT-", credentials!.NetworkName, StringComparison.Ordinal);
        Assert.InRange(credentials.NetworkName.Length, 8, 32);
        Assert.InRange(credentials.Passphrase.Length, 8, 63);
        Assert.True(WifiDirectCredentials.IsUsable(credentials));
    }

    /// <summary>
    /// Nothing that could be misread, and nothing that could be mistaken for punctuation an SSID
    /// field might treat specially.
    /// </summary>
    [Fact]
    public void Nothing_ambiguous_ends_up_in_either_field()
    {
        var credentials = GroupCredentials.ForHost(new FakeIdentity().PublicKey);

        foreach (var c in credentials!.Passphrase + credentials.NetworkName["DIRECT-".Length..])
        {
            Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c), $"unexpected character '{c}'");
            Assert.DoesNotContain(c, "ILOU");   // Crockford drops these — they are misread for 1, 0 and V
        }
    }

    // ── nothing to derive from ─────────────────────────────────────────────

    /// <summary>
    /// A contact whose public key never arrived cannot be met this way. Saying so beats hosting a
    /// group with invented credentials that nobody else on earth could compute.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void Without_a_host_key_there_is_no_group(byte[]? key)
    {
        Assert.Null(GroupCredentials.ForHost(key));
    }

    // ── the pair, end to end ───────────────────────────────────────────────

    /// <summary>
    /// The real scenario. Two phones that have added each other each work out, separately, who hosts
    /// and what the group is called — and land on the same answer with nothing sent between them.
    /// </summary>
    [Fact]
    public void Two_phones_reach_the_same_group_with_nothing_sent()
    {
        var alice = new FakeIdentity();
        var bob = new FakeIdentity();

        // What Alice's phone works out, knowing only her own key and Bob's.
        var aliceHosts = GroupRole.HostsTheGroup(alice.AetherTag, bob.AetherTag);
        var aliceSees = GroupCredentials.ForHost(aliceHosts ? alice.PublicKey : bob.PublicKey);

        // What Bob's phone works out, independently.
        var bobHosts = GroupRole.HostsTheGroup(bob.AetherTag, alice.AetherTag);
        var bobSees = GroupCredentials.ForHost(bobHosts ? bob.PublicKey : alice.PublicKey);

        Assert.NotEqual(aliceHosts, bobHosts);                      // exactly one of them hosts
        Assert.Equal(aliceSees!.NetworkName, bobSees!.NetworkName); // and both look for the same group
        Assert.Equal(aliceSees.Passphrase, bobSees.Passphrase);
    }

    /// <summary>
    /// A third phone joining the same host has to land in the same group as the second, or the Circle
    /// splits into two groups that cannot see each other. This is why the derivation takes the host
    /// alone rather than the pair.
    /// </summary>
    [Fact]
    public void A_third_phone_joins_the_group_that_already_exists()
    {
        var host = new FakeIdentity();

        var second = GroupCredentials.ForHost(host.PublicKey);
        var third = GroupCredentials.ForHost(host.PublicKey);

        Assert.Equal(second!.NetworkName, third!.NetworkName);
        Assert.Equal(second.Passphrase, third.Passphrase);
    }
}

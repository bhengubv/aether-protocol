// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// What happens to an invite between the operating system handing it over and somebody being added.
///
/// <para>
/// Nothing did. The Android activity parsed the link, checked it carried a usable tag, wrote it to a
/// static and raised an event — and no code anywhere subscribed to that event or read that static.
/// Scanning an invite opened the app and added nobody, which is the worst way for it to fail because
/// it is indistinguishable from success.
/// </para>
///
/// <para>
/// The consequence was much larger than one dead button, and is what these are really guarding: an
/// invite is the <b>only</b> thing that carries a public key, and two phones cannot derive the Wi-Fi
/// Direct group they meet on without one. Dropping the link left a fresh pair unable to pair at all.
/// </para>
/// </summary>
public class InviteLinkTests
{
    private const string Invite = "aether://QQQEY-MSMP8/add?k=AOg8g9EZdNs7BoNhTQRzwPFjSky5wwvEhpNTrwIrfAo=";

    [Fact]
    public void A_link_reaches_a_listener()
    {
        var links = new InviteLinks();
        string? got = null;
        links.Arrived += l => got = l;

        links.Deliver(Invite);

        Assert.Equal(Invite, got);
    }

    /// <summary>
    /// The commonest case of the lot: someone points the phone's camera at a QR code and the app is
    /// launched BY the link. It arrives long before there is any UI to hand it to, so throwing it away
    /// for want of a listener would break precisely the journey the feature exists for.
    /// </summary>
    [Fact]
    public void A_link_that_arrives_before_anyone_is_listening_is_kept()
    {
        var links = new InviteLinks();

        links.Deliver(Invite);          // nothing subscribed yet — a cold launch from a scan

        Assert.Equal(Invite, links.TakeWaiting());
    }

    /// <summary>Once taken it is gone, so a later screen cannot add the same person a second time.</summary>
    [Fact]
    public void A_waiting_link_is_only_handed_over_once()
    {
        var links = new InviteLinks();
        links.Deliver(Invite);

        Assert.Equal(Invite, links.TakeWaiting());
        Assert.Null(links.TakeWaiting());
    }

    [Fact]
    public void A_delivered_link_is_not_also_left_waiting()
    {
        var links = new InviteLinks();
        links.Arrived += _ => { };

        links.Deliver(Invite);

        Assert.Null(links.TakeWaiting());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_delivered_and_nothing_is_kept(string? nothing)
    {
        var links = new InviteLinks();
        var calls = 0;
        links.Arrived += _ => calls++;

        links.Deliver(nothing);

        Assert.Equal(0, calls);
        Assert.Null(links.TakeWaiting());
    }

    /// <summary>
    /// The link the relay carries has to be one the contact list can actually use — the tag AND the
    /// key, with the key genuinely deriving the tag. That is the whole reason the invite path matters
    /// more than the typed-tag one.
    /// </summary>
    [Fact]
    public void What_travels_is_a_tag_and_a_key_that_belongs_to_it()
    {
        Assert.True(ContactService.TryParseInvite(Invite, out var tag, out var key));

        Assert.Equal("QQQEY-MSMP8", tag);
        Assert.NotNull(key);
        Assert.NotEmpty(key!);
    }

    /// <summary>
    /// And a typed tag carries no key at all, which is exactly why it cannot bootstrap a radio link
    /// on its own. Worth pinning so the difference between the two paths stays visible.
    /// </summary>
    [Fact]
    public void A_typed_tag_carries_no_key()
    {
        Assert.True(ContactService.TryParseInvite("QQQEY-MSMP8", out var tag, out var key));

        Assert.Equal("QQQEY-MSMP8", tag);
        Assert.Null(key);
    }
}

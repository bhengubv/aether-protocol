// SPDX-License-Identifier: MIT

using AetherNetNodeService.Host;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

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

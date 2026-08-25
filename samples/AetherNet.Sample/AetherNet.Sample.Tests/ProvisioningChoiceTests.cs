// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Taking the least authority a phone will settle for.
///
/// <para>
/// When a tap installs this app, provisioning asks it which mode it wants and supplies the list of
/// modes it will permit. Answering off the list fails the install; not answering fails it too. The
/// answer is therefore ours, decided here, and the whole point is that it is the <i>smallest</i>
/// answer available rather than the largest.
/// </para>
///
/// <para>
/// These tests exist because the failure is silent and one-directional: an app that quietly accepts
/// ownership of somebody's phone works perfectly and nobody finds out until they read a settings
/// screen that says their device is managed by an organisation.
/// </para>
/// </summary>
public class ProvisioningChoiceTests
{
    /// <summary>Offered both, take the smaller.</summary>
    [Fact]
    public void Offered_everything_we_still_take_only_a_profile()
    {
        Assert.Equal(
            ProvisioningChoice.OwnProfileOnly,
            ProvisioningChoice.Least([ProvisioningChoice.WholeDevice, ProvisioningChoice.OwnProfileOnly]));
    }

    /// <summary>Order in the list must not decide it.</summary>
    [Fact]
    public void The_order_they_are_offered_in_changes_nothing()
    {
        Assert.Equal(
            ProvisioningChoice.OwnProfileOnly,
            ProvisioningChoice.Least([ProvisioningChoice.OwnProfileOnly, ProvisioningChoice.WholeDevice]));
    }

    /// <summary>
    /// <b>The one that matters.</b> Offered only the whole phone, we walk away.
    /// </summary>
    /// <remarks>
    /// The tempting behaviour is to take it — the install succeeds, the demo works, and the cost lands
    /// on a stranger who never reads the settings screen that now says their device is managed.
    /// </remarks>
    [Fact]
    public void Offered_only_the_whole_phone_we_refuse()
    {
        Assert.Equal(
            ProvisioningChoice.Refuse,
            ProvisioningChoice.Least([ProvisioningChoice.WholeDevice]));
    }

    /// <summary>An empty list is not an invitation.</summary>
    [Theory]
    [InlineData(new int[0])]
    public void Nothing_offered_is_nothing_taken(int[] allowed)
    {
        Assert.Equal(ProvisioningChoice.Refuse, ProvisioningChoice.Least(allowed));
    }

    /// <summary>And neither is a missing list.</summary>
    [Fact]
    public void A_missing_list_is_refused_rather_than_guessed()
    {
        Assert.Equal(ProvisioningChoice.Refuse, ProvisioningChoice.Least(null));
    }

    /// <summary>
    /// A mode we have never heard of is refused, not accepted for being small.
    /// </summary>
    /// <remarks>
    /// This is why the acceptable set is a list rather than a "take the lowest number" comparison.
    /// Android adds provisioning modes over time, and a future one arriving as a low integer must not
    /// be granted authority over somebody's phone because it happened to sort first.
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_unrecognised_mode_is_refused(int unknown)
    {
        Assert.Equal(ProvisioningChoice.Refuse, ProvisioningChoice.Least([unknown]));
    }

    /// <summary>An unknown mode alongside an acceptable one does not spoil it.</summary>
    [Fact]
    public void An_unknown_mode_beside_a_good_one_is_ignored()
    {
        Assert.Equal(
            ProvisioningChoice.OwnProfileOnly,
            ProvisioningChoice.Least([42, ProvisioningChoice.OwnProfileOnly]));
    }

    /// <summary>Duplicates in the list are not a special case.</summary>
    [Fact]
    public void A_repeated_offer_is_still_one_offer()
    {
        Assert.Equal(
            ProvisioningChoice.OwnProfileOnly,
            ProvisioningChoice.Least(
                [ProvisioningChoice.OwnProfileOnly, ProvisioningChoice.OwnProfileOnly]));
    }

    /// <summary>The values are the ones Android uses, not ones we invented.</summary>
    /// <remarks>
    /// Mirrored rather than referenced so the decision is testable off-device. If they ever drift from
    /// the platform, provisioning fails with no explanation at all — so they are pinned here.
    /// </remarks>
    [Fact]
    public void The_mode_numbers_match_the_platform()
    {
        Assert.Equal(1, ProvisioningChoice.WholeDevice);
        Assert.Equal(2, ProvisioningChoice.OwnProfileOnly);
    }

    /// <summary>Whole-device is recognisable as such, so a refusal can be explained.</summary>
    [Fact]
    public void Taking_the_whole_phone_is_named_as_that()
    {
        Assert.True(ProvisioningChoice.IsTheWholePhone(ProvisioningChoice.WholeDevice));
        Assert.False(ProvisioningChoice.IsTheWholePhone(ProvisioningChoice.OwnProfileOnly));
    }

    /// <summary>
    /// Somebody watching an install stop is told why, in words about their phone.
    /// </summary>
    [Fact]
    public void A_refusal_says_what_was_asked_for()
    {
        var said = ProvisioningChoice.Refusal([ProvisioningChoice.WholeDevice]);

        Assert.Contains("whole device", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was installed", said, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a different refusal when nothing at all was on offer.</summary>
    [Fact]
    public void A_refusal_with_no_offer_says_something_else()
    {
        var said = ProvisioningChoice.Refusal([]);

        Assert.DoesNotContain("whole device", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was installed", said, StringComparison.OrdinalIgnoreCase);
    }
}

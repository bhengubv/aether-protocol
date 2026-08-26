// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Two answers in one touch.
///
/// <para>
/// Android reads the first record of a tap and ignores the rest, so one tap cannot both join a phone
/// to a network and tell it where to go. The credentials half is proven on silicon — a stock handset
/// joins with nobody typing anything — and then it sits there, because the sign-in sheet needs a port
/// Android will not give an app.
/// </para>
///
/// <para>
/// The tag knows the instant a reader finishes taking a message. So it can hold something different
/// by the next read. These tests hold that progression to the one rule that matters: it advances on a
/// completed read and on nothing else.
/// </para>
/// </summary>
public class TapSequenceTests
{
    private static readonly byte[] Network = [1, 2, 3];
    private static readonly byte[] Destination = [4, 5, 6, 7];

    private static TapSequence Armed()
    {
        var tap = new TapSequence();
        tap.Arm(Network, Destination);
        return tap;
    }

    /// <summary>The network goes first, because nothing else is reachable until it has.</summary>
    [Fact]
    public void The_network_is_offered_first()
    {
        var tap = Armed();

        Assert.Equal(TapSequence.Step.Network, tap.At);
        Assert.Equal(Network, tap.Offer);
    }

    /// <summary>
    /// <b>The hinge.</b> A completed read advances what the tag is holding.
    /// </summary>
    [Fact]
    public void A_completed_read_moves_to_the_destination()
    {
        var tap = Armed();

        tap.Taken();

        Assert.Equal(TapSequence.Step.Destination, tap.At);
        Assert.Equal(Destination, tap.Offer);
    }

    /// <summary>And once both are gone there is nothing left to give.</summary>
    [Fact]
    public void Both_taken_leaves_nothing()
    {
        var tap = Armed();

        tap.Taken();
        tap.Taken();

        Assert.Equal(TapSequence.Step.Done, tap.At);
        Assert.Null(tap.Offer);
        Assert.False(tap.HasMore);
    }

    /// <summary>
    /// The phones coming apart does NOT rewind it.
    /// </summary>
    /// <remarks>
    /// Two phones held by hand separate and touch again constantly. A reader that already took the
    /// credentials must get the destination on its next approach, not the credentials again — the
    /// sequence belongs to the handover, not to one continuous field.
    /// </remarks>
    [Fact]
    public void Coming_apart_does_not_rewind()
    {
        var tap = Armed();

        tap.Taken();
        tap.Parted();

        Assert.Equal(TapSequence.Step.Destination, tap.At);
        Assert.Equal(Destination, tap.Offer);
    }

    /// <summary>
    /// A read that never completed leaves the same thing on offer.
    /// </summary>
    /// <remarks>
    /// This is the failure that would be invisible: hand a phone an address on a network it never
    /// joined, and it fetches nothing, forever, with no error anywhere.
    /// </remarks>
    [Fact]
    public void An_unfinished_read_changes_nothing()
    {
        var tap = Armed();

        tap.Parted();
        tap.Parted();

        Assert.Equal(TapSequence.Step.Network, tap.At);
        Assert.Equal(Network, tap.Offer);
    }

    /// <summary>With nowhere to send them, only the network is offered.</summary>
    [Fact]
    public void Without_a_destination_the_network_is_all_there_is()
    {
        var tap = new TapSequence();
        tap.Arm(Network, null);

        Assert.Equal(Network, tap.Offer);

        tap.Taken();

        Assert.Equal(TapSequence.Step.Done, tap.At);
        Assert.Null(tap.Offer);
    }

    /// <summary>A phone already on the network can be handed the destination alone.</summary>
    [Fact]
    public void Without_a_network_the_destination_stands_alone()
    {
        var tap = new TapSequence();
        tap.Arm(null, Destination);

        Assert.Equal(TapSequence.Step.Destination, tap.At);
        Assert.Equal(Destination, tap.Offer);
    }

    /// <summary>Armed with nothing offers nothing.</summary>
    [Fact]
    public void Armed_with_nothing_offers_nothing()
    {
        var tap = new TapSequence();
        tap.Arm(null, null);

        Assert.Equal(TapSequence.Step.Idle, tap.At);
        Assert.Null(tap.Offer);
        Assert.False(tap.HasMore);
    }

    /// <summary>Empty arrays are nothing, not something.</summary>
    [Fact]
    public void Empty_is_treated_as_absent()
    {
        var tap = new TapSequence();
        tap.Arm([], []);

        Assert.Equal(TapSequence.Step.Idle, tap.At);
    }

    /// <summary>Disarming stops it wherever it had got to.</summary>
    [Fact]
    public void Disarming_stops_it()
    {
        var tap = Armed();
        tap.Taken();

        tap.Disarm();

        Assert.Equal(TapSequence.Step.Idle, tap.At);
        Assert.Null(tap.Offer);
    }

    /// <summary>Re-arming starts the whole handover again.</summary>
    [Fact]
    public void Re_arming_starts_over()
    {
        var tap = Armed();
        tap.Taken();
        tap.Taken();

        tap.Arm(Network, Destination);

        Assert.Equal(TapSequence.Step.Network, tap.At);
        Assert.Equal(Network, tap.Offer);
    }

    /// <summary>Each move is announced, so the screen can follow along.</summary>
    [Fact]
    public void Every_move_is_announced()
    {
        var tap = new TapSequence();
        var steps = new List<TapSequence.Step>();
        tap.Moved += steps.Add;

        tap.Arm(Network, Destination);
        tap.Taken();
        tap.Taken();

        Assert.Equal(
            [TapSequence.Step.Network, TapSequence.Step.Destination, TapSequence.Step.Done],
            steps);
    }

    /// <summary>A move that changes nothing is not announced.</summary>
    [Fact]
    public void A_move_that_changes_nothing_is_silent()
    {
        var tap = Armed();
        tap.Taken();
        tap.Taken();

        var after = 0;
        tap.Moved += _ => after++;
        tap.Taken();

        Assert.Equal(0, after);
    }

    /// <summary>Each step can be said out loud to the person holding the phone.</summary>
    [Fact]
    public void Each_step_explains_itself()
    {
        var tap = Armed();
        Assert.Contains("joins your network", tap.Describe(), StringComparison.Ordinal);

        tap.Taken();
        Assert.Contains("Touch again", tap.Describe(), StringComparison.Ordinal);

        tap.Taken();
        Assert.Contains("on its way", tap.Describe(), StringComparison.Ordinal);
    }
}

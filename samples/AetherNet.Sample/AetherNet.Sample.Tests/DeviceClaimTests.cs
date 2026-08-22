// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// One camera, one driver.
///
/// <para>
/// A single object owns the camera, the encoder, the overlay and the table of who is on screen, and
/// it was injected into both call services — each a singleton, each on the radio from startup,
/// neither aware of the other. Seven teardown sites between them. The failure needed no unusual
/// timing: in a group video call, declining an unrelated 1:1 call ran a full teardown and left the
/// group call running with no picture and no error anywhere.
/// </para>
/// </summary>
public class DeviceClaimTests
{
    private sealed class Service(string name)
    {
        public override string ToString() => name;
    }

    [Fact]
    public void Nobody_holds_it_to_begin_with()
        => Assert.False(new DeviceClaim().IsHeld);

    [Fact]
    public void The_first_to_ask_gets_it()
    {
        var claim = new DeviceClaim();
        var oneToOne = new Service("1:1");

        Assert.True(claim.Claim(oneToOne));
        Assert.True(claim.HeldBy(oneToOne));
        Assert.True(claim.IsHeld);
    }

    /// <summary>
    /// The one that matters. A second service asking is told no rather than quietly taking over.
    /// </summary>
    [Fact]
    public void The_second_to_ask_is_refused()
    {
        var claim = new DeviceClaim();
        var group = new Service("group");
        var oneToOne = new Service("1:1");

        Assert.True(claim.Claim(group));

        Assert.False(claim.Claim(oneToOne));
        Assert.False(claim.HeldBy(oneToOne));
        Assert.True(claim.HeldBy(group), "the holder must not lose it to the asking");
    }

    /// <summary>
    /// Reclaiming your own succeeds. Every path that needs the camera claims, and most are reached
    /// more than once in a call — a claim that failed the second time would make a caller's success
    /// depend on how it got there.
    /// </summary>
    [Fact]
    public void Reclaiming_your_own_always_works()
    {
        var claim = new DeviceClaim();
        var group = new Service("group");

        Assert.True(claim.Claim(group));
        Assert.True(claim.Claim(group));
        Assert.True(claim.Claim(group));
        Assert.True(claim.HeldBy(group));
    }

    /// <summary>
    /// The exact bug, stated as a test: the 1:1 call ending must not release a device the group call
    /// is holding.
    /// </summary>
    [Fact]
    public void A_service_that_does_not_hold_it_cannot_release_it()
    {
        var claim = new DeviceClaim();
        var group = new Service("group");
        var oneToOne = new Service("1:1");

        claim.Claim(group);

        Assert.False(claim.Release(oneToOne));
        Assert.True(claim.HeldBy(group), "declining a 1:1 call must not end the group call's video");
        Assert.True(claim.IsHeld);
    }

    /// <summary>
    /// And the return value is what the caller keys the teardown off — false has to mean "touch
    /// nothing", or the guard is decorative.
    /// </summary>
    [Fact]
    public void Releasing_your_own_frees_it_and_says_so()
    {
        var claim = new DeviceClaim();
        var group = new Service("group");
        var oneToOne = new Service("1:1");

        claim.Claim(group);

        Assert.True(claim.Release(group));
        Assert.False(claim.IsHeld);
        Assert.True(claim.Claim(oneToOne), "once given up, the next one may have it");
    }

    // ── asking without taking ──────────────────────────────────────────────

    /// <summary>
    /// A camera button that only discovers the device is busy when it is pressed has already asked
    /// for a permission and produced an error for something it could have known.
    /// </summary>
    [Fact]
    public void You_can_ask_whether_you_could_have_it_without_taking_it()
    {
        var claim = new DeviceClaim();
        var oneToOne = new Service("1:1");

        Assert.True(claim.CanClaim(oneToOne));
        Assert.False(claim.IsHeld, "asking must not take");
    }

    [Fact]
    public void Asking_says_no_while_somebody_else_has_it()
    {
        var claim = new DeviceClaim();
        var group = new Service("group");
        var oneToOne = new Service("1:1");

        claim.Claim(group);

        Assert.False(claim.CanClaim(oneToOne));
        Assert.True(claim.CanClaim(group), "the holder can always reclaim its own");
    }

    [Fact]
    public void Asking_says_yes_again_once_it_is_given_back()
    {
        var claim = new DeviceClaim();
        var group = new Service("group");
        var oneToOne = new Service("1:1");

        claim.Claim(group);
        claim.Release(group);

        Assert.True(claim.CanClaim(oneToOne));
    }

    [Fact]
    public void Releasing_something_nobody_holds_is_harmless()
        => Assert.False(new DeviceClaim().Release(new Service("nobody")));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nothing_is_not_an_owner(bool alreadyHeld)
    {
        var claim = new DeviceClaim();
        if (alreadyHeld) claim.Claim(new Service("someone"));

        Assert.False(claim.Claim(null));
        Assert.False(claim.HeldBy(null));
        Assert.False(claim.Release(null));
    }

    /// <summary>
    /// Two services racing for it from different threads must produce exactly one winner — the camera
    /// cannot be half-owned.
    /// </summary>
    [Fact]
    public void A_race_produces_exactly_one_winner()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var claim = new DeviceClaim();
            var a = new Service("a");
            var b = new Service("b");
            bool gotA = false, gotB = false;

            var start = new ManualResetEventSlim();
            var one = new Thread(() => { start.Wait(); gotA = claim.Claim(a); });
            var two = new Thread(() => { start.Wait(); gotB = claim.Claim(b); });

            one.Start();
            two.Start();
            start.Set();
            one.Join();
            two.Join();

            Assert.True(gotA ^ gotB, "exactly one of them must win");
            Assert.True(claim.HeldBy(gotA ? a : b));
        }
    }
}

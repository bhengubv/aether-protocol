// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The offer clock, and the tap it silently took with it.
///
/// <para>
/// Measured on two handsets: an offer opened at 19:11:53 with a five-minute window, was switched to
/// the heavier tap at 19:13:03, and expired at 19:16:52 — disarming the tag while somebody stood
/// there holding two phones together and getting nothing. Choosing what to hand over costs seconds
/// of reading the installer, so the clock has to answer to the choice rather than outrun it.
/// </para>
/// </summary>
public class AppHandoutExtendTests
{
    private sealed class OneApk : IAppShareService
    {
        public bool IsSupported => true;
        public long SizeBytes => 4;
        public Task<byte[]?> ReadInstallerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>([1, 2, 3, 4]);
        public Task<bool> OfferToInstallAsync(byte[] installer, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>A window that has nearly run out is made whole again.</summary>
    [Fact]
    public void Extending_gives_the_window_back()
    {
        using var handout = new AppHandout(new OneApk(), TimeSpan.FromMilliseconds(400));

        Assert.NotNull(handout.Start(host: "192.168.49.1", from: "aether:someone"));

        Thread.Sleep(250);
        var nearlyGone = handout.Remaining;

        handout.Extend();

        Assert.True(handout.Remaining > nearlyGone,
            $"extending should have given time back; had {nearlyGone}, now {handout.Remaining}");
    }

    /// <summary>
    /// And the offer outlives the moment it would otherwise have died in.
    /// </summary>
    /// <remarks>
    /// The clock is what disarms the tap, so this is the assertion that actually maps to the failure:
    /// past the original expiry, still open.
    /// </remarks>
    [Fact]
    public void An_extended_offer_survives_its_original_expiry()
    {
        var window = TimeSpan.FromSeconds(1);
        using var handout = new AppHandout(new OneApk(), window);

        Assert.NotNull(handout.Start(host: "192.168.49.1", from: "aether:someone"));

        // Most of the window spent, then given back, then most of it spent again — so the moment being
        // survived is past where the first window would have closed.
        Thread.Sleep(700);
        handout.Extend();

        Assert.True(handout.Remaining > window * 0.8,
            $"extending should restore nearly the whole window; got {handout.Remaining}");

        Thread.Sleep(700);

        Assert.True(handout.Remaining > TimeSpan.Zero, "the offer should have outlived its first window");
        Assert.NotNull(handout.Invite);
    }

    /// <summary>Extending something that was never offered opens nothing.</summary>
    [Fact]
    public void Extending_nothing_offers_nothing()
    {
        using var handout = new AppHandout(new OneApk(), TimeSpan.FromMilliseconds(400));

        handout.Extend();

        Assert.Null(handout.Invite);
        Assert.Equal(TimeSpan.Zero, handout.Remaining);
    }

    /// <summary>Anyone watching the countdown is told, rather than left to poll it.</summary>
    [Fact]
    public void Extending_says_so()
    {
        using var handout = new AppHandout(new OneApk(), TimeSpan.FromSeconds(5));
        Assert.NotNull(handout.Start(host: "192.168.49.1", from: "aether:someone"));

        var said = 0;
        handout.Changed += () => said++;

        handout.Extend();

        Assert.Equal(1, said);
    }

    /// <summary>
    /// Once somebody has taken it, the door starts closing.
    /// </summary>
    /// <remarks>
    /// While the group is up, the phone that joined it may be off its own Wi-Fi — measured on a Redmi,
    /// internet gone every time. The package moves in about nine seconds; holding the group for the
    /// rest of a five-minute window turns that into five minutes of no internet for somebody who has
    /// just been given something.
    /// </remarks>
    [Fact]
    public void Closing_soon_brings_the_window_forward()
    {
        using var handout = new AppHandout(new OneApk(), TimeSpan.FromMinutes(5));
        Assert.NotNull(handout.Start(host: "192.168.49.1", from: "Thabang"));

        Assert.True(handout.Remaining > AppHandout.Grace);

        handout.CloseSoon();

        Assert.True(handout.Remaining <= AppHandout.Grace,
            $"should be closing within the grace; {handout.Remaining} left");
        Assert.True(handout.Remaining > TimeSpan.Zero, "but not slammed shut on somebody mid-tap");
    }

    /// <summary>It never extends a window that is already shorter than the grace.</summary>
    [Fact]
    public void Closing_soon_never_gives_time_back()
    {
        using var handout = new AppHandout(new OneApk(), TimeSpan.FromMilliseconds(300));
        Assert.NotNull(handout.Start(host: "192.168.49.1", from: "Thabang"));

        var before = handout.Remaining;
        handout.CloseSoon();

        Assert.True(handout.Remaining <= before, "closing sooner must never mean later");
    }

    /// <summary>And closing an offer that was never open does nothing.</summary>
    [Fact]
    public void Closing_nothing_soon_does_nothing()
    {
        using var handout = new AppHandout(new OneApk(), TimeSpan.FromSeconds(5));

        handout.CloseSoon();

        Assert.Null(handout.Invite);
    }
}

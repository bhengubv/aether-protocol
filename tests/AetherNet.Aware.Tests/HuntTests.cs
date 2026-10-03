// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/domain/HuntTest.kt (cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class HuntTests
{
    private const long Now = 100_000L;

    [Fact]
    public void VeryCloseBeatsCloserWhenScreamingLoud()
    {
        RssiSample[] samples =
        [
            new(Now - 6_000, -70),
            new(Now - 5_000, -68),
            new(Now - 1_200, -38),
            new(Now - 400, -36),
        ];
        Assert.Equal(HuntCue.VeryClose, Hunt.Cue(samples, Now, Now - 400, missing: false));
    }

    [Fact]
    public void BelowVeryCloseStillUsesRelativeCue()
    {
        RssiSample[] samples =
        [
            new(Now - 6_000, -72),
            new(Now - 5_000, -70),
            new(Now - 1_200, -60),
            new(Now - 400, -58),
        ];
        Assert.Equal(HuntCue.Closer, Hunt.Cue(samples, Now, Now - 400, missing: false));
    }

    [Fact]
    public void QuietWinsOverALoudLastPacket()
    {
        RssiSample[] samples = [new(Now - 9_000, -30)];
        Assert.Equal(HuntCue.Quiet, Hunt.Cue(samples, Now, Now - 9_000, missing: false));
    }

    [Fact]
    public void GoneWins()
    {
        Assert.Equal(HuntCue.Gone, Hunt.Cue([], Now, Now, missing: true));
    }

    [Fact]
    public void TickSilentWhenQuietGoneOrNoRssi()
    {
        Assert.Null(Hunt.TickIntervalMs(-40, HuntCue.Quiet));
        Assert.Null(Hunt.TickIntervalMs(-40, HuntCue.Gone));
        Assert.Null(Hunt.TickIntervalMs(null, HuntCue.Closer));
    }

    [Fact]
    public void TickFasterWhenLouder()
    {
        var slow = Hunt.TickIntervalMs(-90, HuntCue.Same)!.Value;
        var mid = Hunt.TickIntervalMs(-65, HuntCue.Closer)!.Value;
        var fast = Hunt.TickIntervalMs(-40, HuntCue.VeryClose)!.Value;
        Assert.Equal(Hunt.TickSlowMs, slow);
        Assert.Equal(Hunt.TickFastMs, fast);
        Assert.InRange(mid, fast + 1, slow - 1);
        var veryClose = Hunt.TickIntervalMs(-45, HuntCue.VeryClose)!.Value;
        Assert.True(veryClose < mid);
        Assert.True(veryClose > fast);
    }
}

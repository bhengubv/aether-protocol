// SPDX-License-Identifier: MIT
// The device's own walk, as FieldwatchApp.recordOperatorFix / acceptFix keep it (cf6562d). Fieldwatch has no tests
// for it; these pin the ported behaviour.

using Xunit;

namespace AetherNet.Aware.Tests;

public class OperatorPathTests
{
    private const double Lat0 = -26.2041;
    private const double Lon0 = 28.0473;
    private const double Step = 20.0 / 111_195.0; // 20 m of latitude

    private readonly ManualClock _clock = new();

    [Fact]
    public void AcceptsAGoodFixAndRefusesAVagueOrOldOne()
    {
        var path = new OperatorPath(_clock);
        Assert.True(path.Accept(Lat0, Lon0, _clock.NowMs, accuracyM: 10));
        Assert.False(path.Accept(Lat0 + Step, Lon0, _clock.NowMs, accuracyM: 120));
        Assert.False(path.Accept(Lat0 + Step, Lon0, _clock.NowMs - 31_000, accuracyM: 10));
        Assert.True(path.Accept(Lat0 + Step, Lon0, _clock.NowMs, accuracyM: null));
        Assert.Equal(2, path.Copy().Count);
    }

    [Fact]
    public void ACloseFixReplacesTheLastPointInsteadOfAddingOne()
    {
        var path = new OperatorPath(_clock);
        path.Record(Lat0, Lon0, 1_000);
        path.Record(Lat0 + Step / 4, Lon0, 2_000); // 5 m away
        var points = path.Copy();
        Assert.Single(points);
        Assert.Equal(2_000, points[0].At);
        Assert.Equal(0.0, path.LengthM);
    }

    [Fact]
    public void LengthGrowsWithTheWalkAndATeleportIsIgnored()
    {
        var path = new OperatorPath(_clock);
        for (var i = 0; i < 5; i++)
        {
            path.Record(Lat0 + i * Step, Lon0, 1_000 + i * 20_000L);
        }
        Assert.Equal(80.0, path.LengthM, 0);
        path.Record(Lat0 + 1.0, Lon0, 1_000 + 4 * 20_000L + 2_000); // 111 km two seconds later
        Assert.Equal(5, path.Copy().Count);
        Assert.Equal(80.0, path.LengthM, 0);
    }

    [Fact]
    public void KeepsTheLatestEightyPoints()
    {
        var path = new OperatorPath(_clock);
        for (var i = 0; i < 100; i++)
        {
            path.Record(Lat0 + i * Step, Lon0, 1_000 + i * 20_000L);
        }
        var points = path.Copy();
        Assert.Equal(OperatorPath.MaxPoints, points.Count);
        Assert.Equal(1_000 + 99 * 20_000L, points[^1].At);
        Assert.Equal(1_000 + 20 * 20_000L, points[0].At);
        Assert.Equal(79 * 20.0, path.LengthM, 0);
    }

    [Fact]
    public void ResetForgetsTheWalk()
    {
        var path = new OperatorPath(_clock);
        path.Record(Lat0, Lon0, 1_000);
        path.Record(Lat0 + Step, Lon0, 21_000);
        path.Reset();
        Assert.Empty(path.Copy());
        Assert.Equal(0.0, path.LengthM);
    }
}

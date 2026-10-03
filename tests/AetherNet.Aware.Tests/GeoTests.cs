// SPDX-License-Identifier: MIT
// Fieldwatch (cf6562d) tests Geo only through its sit plots and privacy masking, neither of which is ported; these
// pin the parts Aether Aware uses (distance, the GPS-glitch filter, and how a device's trail grows).

using Xunit;

namespace AetherNet.Aware.Tests;

public class GeoTests
{
    private const long T0 = 1_790_000_000_000;

    [Fact]
    public void OneDegreeOfLatitudeIsAbout111Km()
    {
        Assert.Equal(111_195, Geo.Meters(0, 0, 1, 0), 0);
        Assert.Equal(0.0, Geo.Meters(-26.2, 28.0, -26.2, 28.0));
    }

    [Fact]
    public void PathLengthAddsTheHops()
    {
        GpsSample[] path = [new(T0, 0, 0), new(T0 + 1, 0.001, 0), new(T0 + 2, 0.002, 0)];
        Assert.Equal(Geo.Meters(0, 0, 0.002, 0), Geo.PathLengthM(path), 6);
        Assert.Equal(0.0, Geo.PathLengthM([new(T0, 0, 0)]));
    }

    [Fact]
    public void HopPlausibleRejectsATeleport()
    {
        var from = new GpsSample(T0, 0, 0);
        Assert.True(Geo.HopPlausible(from, 0.0003, 0, T0 + 1_000));   // 33 m in a second: under the 40 m floor
        Assert.True(Geo.HopPlausible(from, 0.01, 0, T0 + 500));       // 1.1 km in half a second: too soon to judge
        Assert.False(Geo.HopPlausible(from, 0.01, 0, T0 + 2_000));    // 1.1 km in two seconds
        Assert.True(Geo.HopPlausible(from, 0.01, 0, T0 + 60_000));    // 1.1 km in a minute: driving
    }

    [Fact]
    public void DespikeDropsAPointThatShootsOutAndBack()
    {
        GpsSample[] path =
        [
            new(T0, 0, 0),
            new(T0 + 60_000, 0.0005, 0),
            new(T0 + 120_000, 0.01, 0),      // a glitch 1 km away
            new(T0 + 180_000, 0.0006, 0),
            new(T0 + 240_000, 0.0007, 0),
        ];
        var clean = Geo.DespikePath(path);
        Assert.Equal(4, clean.Count);
        Assert.DoesNotContain(clean, p => p.Lat == 0.01);
    }

    [Fact]
    public void AppendSkipsSmallMovesAndKeepsReal()
    {
        IReadOnlyList<GpsSample> trail = [new(T0, 0, 0, -60)];
        Assert.Same(trail, Geo.Append(trail, T0 + 5_000, 0.00005, 0, -60));          // ~5.6 m: under 8 m
        Assert.Same(trail, Geo.Append(trail, T0 + 5_000, 0.00012, 0, -60));          // ~13 m within 30 s
        Assert.Equal(2, Geo.Append(trail, T0 + 31_000, 0.00012, 0, -60).Count);      // ~13 m after 30 s
        Assert.Equal(2, Geo.Append(trail, T0 + 5_000, 0.0002, 0, -60).Count);        // ~22 m
    }

    [Fact]
    public void CapSpreadKeepsTheEndsAndSpreadsTheRest()
    {
        var samples = Enumerable.Range(0, 100).Select(i => new GpsSample(T0 + i, i * 0.001, 0)).ToList();
        var capped = Geo.CapSpread(samples, 10);
        Assert.Equal(10, capped.Count);
        Assert.Equal(samples[0], capped[0]);
        Assert.Equal(samples[^1], capped[^1]);
        Assert.Equal([samples[^1]], Geo.CapSpread(samples, 1));
        Assert.Equal([samples[0], samples[^1]], Geo.CapSpread(samples, 2));
        Assert.Same(samples, Geo.CapSpread(samples, 100));
    }
}

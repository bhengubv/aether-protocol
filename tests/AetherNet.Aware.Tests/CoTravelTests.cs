// SPDX-License-Identifier: MIT
// Fieldwatch (cf6562d) has no tests for CoTravel ("Moving with you"); these pin the behaviour of its code
// (domain/Geo.kt) as ported. A walk heads north from the same point in every test: 1e-4 degrees of latitude
// is about 11.1 m.

using Xunit;

namespace AetherNet.Aware.Tests;

public class CoTravelTests
{
    private const double Lat0 = -26.2041;
    private const double Lon0 = 28.0473;
    private const long Start = 1_790_000_000_000;

    [Fact]
    public void ATagCarriedOnTheWalkIsWithYou()
    {
        var (path, now) = Walk(points: 20, metresPerStep: 11.1, msPerStep: 10_000);
        var tag = Tag(trailFrom: path, rssi: -60, lastSeen: now);
        Assert.True(CoTravel.Ctx.Of(path).Ready);
        Assert.True(CoTravel.WithYou(tag, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void WifiNeverMovesWithYou()
    {
        var (path, now) = Walk(points: 20, metresPerStep: 11.1, msPerStep: 10_000);
        var ap = Tag(trailFrom: path, rssi: -60, lastSeen: now) with { Kind = RadioKind.Wifi, Key = "WIFI:AA:BB:CC:DD:EE:01" };
        Assert.False(CoTravel.WithYou(ap, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void AShortWalkIsNotEnoughToTell()
    {
        // Three steps of ~11 m: 33 m walked, under the 45 m it takes.
        var (path, now) = Walk(points: 4, metresPerStep: 11.1, msPerStep: 10_000);
        var ctx = CoTravel.Ctx.Of(path);
        Assert.False(ctx.Ready);
        Assert.False(CoTravel.WithYou(Tag(trailFrom: path, rssi: -60, lastSeen: now), ctx, now));
        Assert.False(CoTravel.Ctx.None.Ready);
    }

    [Fact]
    public void ATagLeftBehindStopsBeingWithYou()
    {
        // Carried for the first half of the walk, then left on a bench: not heard again.
        var (path, now) = Walk(points: 30, metresPerStep: 11.1, msPerStep: 10_000);
        var carried = path.Take(15).ToList();
        var tag = Tag(trailFrom: carried, rssi: -60, lastSeen: carried[^1].At);
        Assert.True(now - tag.LastSeen > CoTravel.HeardMs);
        Assert.False(CoTravel.WithYou(tag, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void ATagStillFaintlyHeardFarBehindIsNotWithYou()
    {
        // Heard a moment ago, but its last stamp is where it was left, about 170 m back.
        var (path, now) = Walk(points: 30, metresPerStep: 11.1, msPerStep: 3_000);
        var carried = path.Take(15).ToList();
        var tag = Tag(trailFrom: carried, rssi: -60, lastSeen: now);
        Assert.False(CoTravel.WithYou(tag, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void AQuietTagIsNotWithYou()
    {
        var (path, now) = Walk(points: 20, metresPerStep: 11.1, msPerStep: 10_000);
        var faint = Tag(trailFrom: path, rssi: -80, lastSeen: now);
        Assert.False(CoTravel.WithYou(faint, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void ATrailThatWasMostlyFaintIsNotWithYou()
    {
        // Loud now, but only 5 of its 20 stamps were loud: two thirds are needed.
        var (path, now) = Walk(points: 20, metresPerStep: 11.1, msPerStep: 10_000);
        var trail = path.Select((p, i) => p with { Rssi = i < 15 ? -85 : -60 }).ToList();
        var tag = Tag(trailFrom: path, rssi: -60, lastSeen: now) with { GpsTrail = trail };
        Assert.False(CoTravel.WithYou(tag, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void ATagHeardInOneSpotIsNotWithYou()
    {
        // A beacon at the cafe where the walk started: its trail never left the table.
        var (path, now) = Walk(points: 20, metresPerStep: 11.1, msPerStep: 10_000);
        var cafe = Tag(trailFrom: [path[0], path[0] with { At = path[0].At + 5_000 }], rssi: -60, lastSeen: now);
        Assert.False(CoTravel.WithYou(cafe, CoTravel.Ctx.Of(path), now));
    }

    [Fact]
    public void DrivingWidensHowFarBehindTheLastStampMayBe()
    {
        // The last stamp is about 300 m back. Walking (~1 m/s) allows 75 m; driving at 30 m/s allows 475 m.
        var (walk, walkNow) = Walk(points: 60, metresPerStep: 11.1, msPerStep: 10_000);
        var walkTag = Tag(trailFrom: walk.Take(33).ToList(), rssi: -60, lastSeen: walkNow);
        Assert.False(CoTravel.WithYou(walkTag, CoTravel.Ctx.Of(walk), walkNow));

        var (drive, driveNow) = Walk(points: 60, metresPerStep: 111.0, msPerStep: 3_700);
        var carTag = Tag(trailFrom: drive.Take(57).ToList(), rssi: -60, lastSeen: driveNow);
        Assert.True(CoTravel.WithYou(carTag, CoTravel.Ctx.Of(drive), driveNow));
    }

    private static (List<GpsSample> Path, long Now) Walk(int points, double metresPerStep, long msPerStep)
    {
        var degPerStep = metresPerStep / 111_195.0;
        var path = Enumerable.Range(0, points)
            .Select(i => new GpsSample(Start + i * msPerStep, Lat0 + i * degPerStep, Lon0, -60))
            .ToList();
        return (path, path[^1].At);
    }

    private static Sighting Tag(IReadOnlyList<GpsSample> trailFrom, int rssi, long lastSeen) => new()
    {
        Key = "BLE:AA:BB:CC:DD:EE:01",
        Kind = RadioKind.Ble,
        Mac = "AA:BB:CC:DD:EE:01",
        Rssi = rssi,
        RssiMin = rssi,
        RssiMax = rssi,
        FirstSeen = trailFrom[0].At,
        LastSeen = lastSeen,
        HitCount = trailFrom.Count,
        GpsTrail = trailFrom.Select(p => p with { Rssi = rssi }).ToList(),
    };
}

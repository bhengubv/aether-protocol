// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Aware.Tests;

public class HelpWatchTests
{
    private const double Lat0 = -26.2041;
    private const double Lon0 = 28.0473;
    private const double Metre = 1.0 / 111_195.0; // of latitude

    private readonly ManualClock _clock = new();
    private readonly HelpKey _key = HelpKey.Create();
    private readonly HelpWatch _watch;

    public HelpWatchTests()
    {
        _watch = new HelpWatch(_clock);
        _watch.Watch("person-1", "Thandi", _key);
    }

    [Fact]
    public void AGuardianSeesHelpAndTheTrailGrows()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        HelpCase? seen = null;
        for (var i = 0; i < 3; i++)
        {
            session.UpdatePosition(Lat0 + (i * 30 * Metre), Lon0, _clock.NowMs, accuracyM: 6);
            seen = _watch.Hear(session.NextMessage(), rssi: -70);
            _clock.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.NotNull(seen);
        Assert.Equal("person-1", seen.PersonId);
        Assert.Equal("Thandi", seen.Name);
        Assert.Equal(HelpKind.Help, seen.Kind);
        Assert.Equal(3, seen.Trail.Count);
        Assert.All(seen.Trail, c => Assert.Equal(CrumbSource.Reported, c.Source));
        Assert.Equal(Lat0 + (60 * Metre), seen.Lat!.Value, 2e-5);
        Assert.Same(seen, _watch.Find("person-1"));
    }

    [Fact]
    public void AMessageFromSomeoneElseKeepsNothing()
    {
        var stranger = new HelpSession(HelpKey.Create(), HelpKind.Help, _clock);
        stranger.UpdatePosition(Lat0, Lon0, _clock.NowMs, accuracyM: 5);
        Assert.Null(_watch.Hear(stranger.NextMessage(), rssi: -60));
        Assert.Null(_watch.Hear(new byte[HelpCodec.Length], rssi: -60));
        Assert.Empty(_watch.Cases);
    }

    [Fact]
    public void ARepeatedAdvertAddsLoudnessNotPoints()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(Lat0, Lon0, _clock.NowMs, accuracyM: 5);
        var payload = session.NextMessage();
        foreach (var rssi in new[] { -60, -58, -57 })
        {
            _watch.Hear(payload, rssi);
            _clock.Advance(TimeSpan.FromMilliseconds(200));
        }
        var c = _watch.Find("person-1")!;
        Assert.Single(c.Trail);
        Assert.Equal([-60, -58, -57], c.Loudness.Select(s => s.Rssi));
        Assert.NotNull(c.LastNearbyAt);
    }

    [Fact]
    public void OverTheMeshThereIsNoLoudness()
    {
        var session = new HelpSession(_key, HelpKind.Walk, _clock);
        session.UpdatePosition(Lat0, Lon0, _clock.NowMs, accuracyM: 5);
        var c = _watch.Hear(session.NextMessage())!;
        Assert.Equal(HelpKind.Walk, c.Kind);
        Assert.Empty(c.Loudness);
        Assert.Null(c.LastNearbyAt);
        Assert.Single(c.Trail);
    }

    [Fact]
    public void AnOlderMessageHeardLateIsIgnored()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(Lat0, Lon0, _clock.NowMs, accuracyM: 5);
        var older = session.NextMessage();
        _clock.Advance(TimeSpan.FromSeconds(10));
        session.UpdatePosition(Lat0 + (50 * Metre), Lon0, _clock.NowMs, accuracyM: 5);
        var newer = session.NextMessage();

        _watch.Hear(newer);
        var c = _watch.Hear(older)!;
        Assert.Equal(Lat0 + (50 * Metre), c.Lat!.Value, 2e-5);
        Assert.Single(c.Trail);
    }

    [Fact]
    public void WithNoPositionTheGuardianNotesWhereItHeardThem()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        var here = new GpsSample(_clock.NowMs, Lat0, Lon0);
        var c = _watch.Hear(session.NextMessage(), rssi: -65, whereIAm: here)!;
        var crumb = Assert.Single(c.Trail);
        Assert.Equal(CrumbSource.HeardNear, crumb.Source);
        Assert.Equal(Lat0, crumb.Lat);
        Assert.Equal(-65, crumb.Rssi);
        Assert.Null(c.Lat);
    }

    [Fact]
    public void TinyMovesDoNotAddPoints()
    {
        var session = new HelpSession(_key, HelpKind.Walk, _clock);
        for (var i = 0; i < 4; i++)
        {
            session.UpdatePosition(Lat0 + (i * Metre), Lon0, _clock.NowMs, accuracyM: 5);
            _watch.Hear(session.NextMessage());
            _clock.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.Single(_watch.Find("person-1")!.Trail);
    }

    [Fact]
    public void SafeEndsTheCaseAndADayLaterItIsForgotten()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(Lat0, Lon0, _clock.NowMs, accuracyM: 5);
        _watch.Hear(session.NextMessage());
        _clock.Advance(TimeSpan.FromSeconds(30));
        session.MarkSafe();
        var c = _watch.Hear(session.NextMessage())!;
        Assert.True(c.IsSafe);
        Assert.Equal(_clock.NowMs, c.SafeAt);

        _clock.Advance(TimeSpan.FromHours(23));
        _watch.Refresh();
        Assert.NotNull(_watch.Find("person-1"));
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        _watch.Refresh();
        Assert.Null(_watch.Find("person-1"));
    }

    [Fact]
    public void NewHelpAfterSafeStartsAFreshCase()
    {
        var first = new HelpSession(_key, HelpKind.Help, _clock);
        first.UpdatePosition(Lat0, Lon0, _clock.NowMs, accuracyM: 5);
        _watch.Hear(first.NextMessage());
        _clock.Advance(TimeSpan.FromSeconds(5));
        first.MarkSafe();
        _watch.Hear(first.NextMessage());

        _clock.Advance(TimeSpan.FromMinutes(1));
        var again = new HelpSession(_key, HelpKind.Help, _clock);
        again.UpdatePosition(Lat0 + (200 * Metre), Lon0, _clock.NowMs, accuracyM: 5);
        var c = _watch.Hear(again.NextMessage())!;
        Assert.False(c.IsSafe);
        Assert.Null(c.SafeAt);
        Assert.Single(c.Trail);
        Assert.Equal(_clock.NowMs, c.FirstHeardAt);
    }

    [Fact]
    public void FindItSaysCloserAsItGetsLouder()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        var payload = session.NextMessage();
        _watch.Hear(payload, rssi: -75);
        _clock.Advance(TimeSpan.FromMilliseconds(1_000));
        _watch.Hear(payload, rssi: -74);
        _clock.Advance(TimeSpan.FromMilliseconds(3_800));
        _watch.Hear(payload, rssi: -62);
        _clock.Advance(TimeSpan.FromMilliseconds(800));
        _watch.Hear(payload, rssi: -60);
        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(HuntCue.Closer, _watch.Find("person-1")!.FindIt(_clock.NowMs));
    }

    [Fact]
    public void ALongTrailKeepsItsEndsAndStaysBounded()
    {
        var session = new HelpSession(_key, HelpKind.Walk, _clock);
        for (var i = 0; i < 250; i++)
        {
            session.UpdatePosition(Lat0 + (i * 20 * Metre), Lon0, _clock.NowMs, accuracyM: 5);
            _watch.Hear(session.NextMessage());
            _clock.Advance(TimeSpan.FromSeconds(5));
        }
        var trail = _watch.Find("person-1")!.Trail;
        Assert.Equal(HelpWatch.TrailCap, trail.Count);
        Assert.Equal(Lat0, trail[0].Lat, 2e-5);
        Assert.Equal(Lat0 + (249 * 20 * Metre), trail[^1].Lat, 2e-5);
    }

    [Fact]
    public void NoLongerAGuardianForgetsThePerson()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        _watch.Hear(session.NextMessage());
        _watch.Unwatch("person-1");
        Assert.Empty(_watch.Cases);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_watch.Hear(session.NextMessage()));
    }
}

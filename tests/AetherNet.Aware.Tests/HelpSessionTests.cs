// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Aware.Tests;

public class HelpSessionTests
{
    private const double Lat = -26.2041;
    private const double Lon = 28.0473;

    private readonly ManualClock _clock = new();
    private readonly HelpKey _key = HelpKey.Create();

    [Fact]
    public void TheMessageCarriesTheLatestPositionAndBattery()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(Lat, Lon, _clock.NowMs - 3_000, accuracyM: 8.2);
        session.UpdateBattery(64);
        var m = Read(session.NextMessage());
        Assert.Equal(HelpKind.Help, m.Kind);
        Assert.Equal(Lat, m.Lat!.Value, 2e-5);
        Assert.Equal(Lon, m.Lon!.Value, 2e-5);
        Assert.Equal(10, m.AccuracyM);
        Assert.Equal(64, m.BatteryPercent);
        Assert.Equal(3, m.FixAgeSeconds);
    }

    [Fact]
    public void OneStepOneMessage()
    {
        var session = new HelpSession(_key, HelpKind.Walk, _clock);
        session.UpdatePosition(Lat, Lon, _clock.NowMs, accuracyM: 5);
        var first = session.NextMessage();
        session.UpdatePosition(Lat + 0.001, Lon, _clock.NowMs, accuracyM: 5);
        Assert.Equal(first, session.NextMessage());
        _clock.Advance(TimeSpan.FromMilliseconds(HelpCodec.StepMs));
        Assert.NotEqual(first, session.NextMessage());
    }

    [Fact]
    public void AnOlderFixOrNoRealPositionIsIgnored()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(0, 0, _clock.NowMs, accuracyM: 5);
        Assert.Null(Read(session.NextMessage()).Lat);

        session.UpdatePosition(Lat, Lon, _clock.NowMs, accuracyM: 5);
        session.UpdatePosition(Lat + 0.01, Lon, _clock.NowMs - 10_000, accuracyM: 5);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Lat, Read(session.NextMessage()).Lat!.Value, 2e-5);
    }

    [Fact]
    public void SafeIsSentForAMinuteThenItIsOver()
    {
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.MarkSafe();
        Assert.Equal(HelpKind.Safe, session.Kind);
        Assert.Equal(HelpKind.Safe, Read(session.NextMessage()).Kind);
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(session.IsOver);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(session.IsOver);
    }

    [Fact]
    public void ASessionCannotStartAsSafe()
    {
        Assert.Throws<ArgumentException>(() => new HelpSession(_key, HelpKind.Safe, _clock));
    }

    private HelpMessage Read(byte[] payload) => HelpCodec.TryRead(_key, payload, _clock.NowMs)!.Message;
}

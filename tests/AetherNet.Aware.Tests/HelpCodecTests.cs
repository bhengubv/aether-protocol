// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Aware.Tests;

public class HelpCodecTests
{
    private const double Lat = -26.2041;
    private const double Lon = 28.0473;
    private static readonly long T0 = new ManualClock().NowMs;

    private static readonly HelpMessage Sample = new()
    {
        Kind = HelpKind.Help,
        Lat = Lat,
        Lon = Lon,
        AccuracyM = 9,
        BatteryPercent = 64,
        FixAgeSeconds = 3,
    };

    [Fact]
    public void AGuardianReadsWhatThePersonSent()
    {
        var key = HelpKey.Create();
        var payload = HelpCodec.Encode(key, Sample, T0);
        var reading = HelpCodec.TryRead(key, payload, T0);
        Assert.NotNull(reading);
        var m = reading.Message;
        Assert.Equal(HelpKind.Help, m.Kind);
        Assert.Equal(Lat, m.Lat!.Value, 2e-5);
        Assert.Equal(Lon, m.Lon!.Value, 2e-5);
        Assert.Equal(10, m.AccuracyM);
        Assert.Equal(64, m.BatteryPercent);
        Assert.Equal(3, m.FixAgeSeconds);
        Assert.InRange(reading.MadeAt, T0 - HelpCodec.StepMs + 1, T0);
    }

    [Fact]
    public void ItFitsAStandardBluetoothAdvert()
    {
        Assert.Equal(23, HelpCodec.Length);
        Assert.Equal(HelpCodec.Length, HelpCodec.Encode(HelpKey.Create(), Sample, T0).Length);
        Assert.True(3 + 4 + HelpCodec.Length <= 31);
    }

    [Fact]
    public void AnotherKeyReadsNothing()
    {
        var payload = HelpCodec.Encode(HelpKey.Create(), Sample, T0);
        Assert.Null(HelpCodec.TryRead(HelpKey.Create(), payload, T0));
    }

    [Fact]
    public void AnyAlteredByteIsRefused()
    {
        var key = HelpKey.Create();
        var payload = HelpCodec.Encode(key, Sample, T0);
        for (var i = 0; i < payload.Length; i++)
        {
            var altered = (byte[])payload.Clone();
            altered[i] ^= 0x01;
            Assert.Null(HelpCodec.TryRead(key, altered, T0));
        }
    }

    [Fact]
    public void TheLabelChangesEachQuarterHourAndTheSealedPartEachMessage()
    {
        var key = HelpKey.Create();
        var first = HelpCodec.Encode(key, Sample, T0);
        var second = HelpCodec.Encode(key, Sample, T0 + 1_000);
        var nextQuarter = HelpCodec.Encode(key, Sample, T0 + HelpCodec.QuarterHourMs);
        Assert.Equal(first[1..7], second[1..7]);
        Assert.NotEqual(first[9..19], second[9..19]);
        Assert.NotEqual(first[1..7], nextQuarter[1..7]);
    }

    [Fact]
    public void ClocksAQuarterHourApartStillAgreeButNotMore()
    {
        var key = HelpKey.Create();
        var payload = HelpCodec.Encode(key, Sample, T0);
        Assert.NotNull(HelpCodec.TryRead(key, payload, T0 + (14 * 60_000)));
        Assert.NotNull(HelpCodec.TryRead(key, payload, T0 - (14 * 60_000)));
        Assert.Null(HelpCodec.TryRead(key, payload, T0 + (31 * 60_000)));
    }

    [Fact]
    public void NoPositionReadsAsNone()
    {
        var key = HelpKey.Create();
        var payload = HelpCodec.Encode(key, new HelpMessage { Kind = HelpKind.Walk, BatteryPercent = 12 }, T0);
        var m = HelpCodec.TryRead(key, payload, T0)!.Message;
        Assert.Equal(HelpKind.Walk, m.Kind);
        Assert.Null(m.Lat);
        Assert.Null(m.Lon);
        Assert.Null(m.FixAgeSeconds);
        Assert.Null(m.AccuracyM);
        Assert.Equal(12, m.BatteryPercent);
    }

    [Fact]
    public void ValuesOutOfRangeAreHeldToTheEdges()
    {
        var key = HelpKey.Create();
        var wild = new HelpMessage { Kind = HelpKind.Help, Lat = 95, Lon = Lon, AccuracyM = 1_000, BatteryPercent = 150, FixAgeSeconds = 999 };
        var m = HelpCodec.TryRead(key, HelpCodec.Encode(key, wild, T0), T0)!.Message;
        Assert.Null(m.Lat);
        Assert.Equal(508, m.AccuracyM);
        Assert.Equal(100, m.BatteryPercent);
        Assert.Null(m.FixAgeSeconds);

        var old = HelpCodec.TryRead(key, HelpCodec.Encode(key, Sample with { FixAgeSeconds = 999 }, T0), T0)!.Message;
        Assert.Equal(254, old.FixAgeSeconds);
    }

    [Fact]
    public void WrongSizeOrFormatIsNotAHelpMessage()
    {
        var key = HelpKey.Create();
        var payload = HelpCodec.Encode(key, Sample, T0);
        Assert.Null(HelpCodec.TryRead(key, payload[..22], T0));
        var otherFormat = (byte[])payload.Clone();
        otherFormat[0] = 0x02;
        Assert.Null(HelpCodec.TryRead(key, otherFormat, T0));
    }

    [Fact]
    public void AKeyTravelsAsBytes()
    {
        var key = HelpKey.Create();
        var copy = HelpKey.FromBytes(key.ToBytes());
        Assert.NotNull(HelpCodec.TryRead(copy, HelpCodec.Encode(key, Sample, T0), T0));
        Assert.NotEqual(key.ToBytes(), HelpKey.Create().ToBytes());
        Assert.Throws<ArgumentException>(() => HelpKey.FromBytes(new byte[31]));
    }
}

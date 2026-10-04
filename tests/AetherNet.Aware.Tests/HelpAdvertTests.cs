// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Aware.Tests;

public class HelpAdvertTests
{
    private const ushort RegisteredId = 0xFD6F;
    private readonly ManualClock _clock = new();
    private readonly HelpKey _key = HelpKey.Create();

    private byte[] Message() => HelpCodec.Encode(_key, new HelpMessage { Kind = HelpKind.Help, BatteryPercent = 40 }, _clock.NowMs);

    [Fact]
    public void TheStandardAdvertFitsAndTheHundredAndTwentyEightBitOneDoesNot()
    {
        Assert.True(HelpAdvert.FitsLegacyAdvert(HelpAdvertForm.Registered16));
        Assert.Equal(30, HelpAdvert.Size(HelpAdvertForm.Registered16));
        Assert.False(HelpAdvert.FitsLegacyAdvert(HelpAdvertForm.AetherNet128));
        Assert.Equal(44, HelpAdvert.Size(HelpAdvertForm.AetherNet128));
    }

    [Fact]
    public void BothFormsCarryTheSameMessage()
    {
        var message = Message();
        foreach (var form in new[] { HelpAdvertForm.Registered16, HelpAdvertForm.AetherNet128 })
        {
            var advert = HelpAdvert.Build(form, message, RegisteredId);
            Assert.Equal(HelpAdvert.Size(form), advert.Length);
            Assert.Equal(message, HelpAdvert.TryFind(advert, RegisteredId));
        }
    }

    [Fact]
    public void TheRadiosReadItAsOrdinaryServiceData()
    {
        // What the service gives Aware for every advert it hears.
        var message = Message();
        foreach (var form in new[] { HelpAdvertForm.Registered16, HelpAdvertForm.AetherNet128 })
        {
            var parsed = BleAdParser.Parse(HelpAdvert.Build(form, message, RegisteredId));
            Assert.Equal(0x06, parsed.Flags);
            var facts = new RadioFacts { ServiceData = parsed.ServiceData };
            Assert.Equal(message, HelpAdvert.TryFind(facts, RegisteredId));
        }
    }

    [Fact]
    public void TheStandardFormWaitsForARegisteredId()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => HelpAdvert.Build(HelpAdvertForm.Registered16, Message()));
        Assert.Contains("registered", thrown.Message, StringComparison.OrdinalIgnoreCase);
        // Ours needs nobody's permission.
        Assert.Equal(44, HelpAdvert.Build(HelpAdvertForm.AetherNet128, Message()).Length);
    }

    [Fact]
    public void AnotherIdOrAnotherAdvertIsNotAHelpMessage()
    {
        var advert = HelpAdvert.Build(HelpAdvertForm.Registered16, Message(), RegisteredId);
        Assert.Null(HelpAdvert.TryFind(advert, registeredId: 0x1234));
        Assert.Null(HelpAdvert.TryFind(advert));
        // An Eddystone beacon, which is what the air is full of.
        var eddystone = new RadioFacts { ServiceData = [new("FEAA", "20AB")] };
        Assert.Null(HelpAdvert.TryFind(eddystone, RegisteredId));
        Assert.Null(HelpAdvert.TryFind(new RadioFacts(), RegisteredId));
    }

    [Fact]
    public void ServiceDataOfTheWrongLengthIsNotAHelpMessage()
    {
        var facts = new RadioFacts { ServiceData = [new(HelpAdvert.ServiceUuid.ToString(), "0102")] };
        Assert.Null(HelpAdvert.TryFind(facts));
        Assert.Throws<ArgumentException>(() => HelpAdvert.Build(HelpAdvertForm.AetherNet128, new byte[22]));
    }

    [Fact]
    public void AGuardianReadsAnAdvertStraightFromTheRadios()
    {
        var watch = new HelpWatch(_clock);
        watch.Watch("person-1", "Thandi", _key);
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(-26.2041, 28.0473, _clock.NowMs, accuracyM: 7);

        var advert = HelpAdvert.Build(HelpAdvertForm.AetherNet128, session.NextMessage());
        var facts = new RadioFacts { ServiceData = BleAdParser.Parse(advert).ServiceData };
        var seen = watch.Hear(facts, rssi: -58);

        Assert.NotNull(seen);
        Assert.Equal("Thandi", seen.Name);
        Assert.Equal(HelpKind.Help, seen.Kind);
        Assert.Equal(-26.2041, seen.Lat!.Value, 2e-5);
        Assert.Single(seen.Trail);

        // Somebody else's advert, in the same container, stays unreadable.
        var stranger = new HelpSession(HelpKey.Create(), HelpKind.Help, _clock);
        var theirs = HelpAdvert.Build(HelpAdvertForm.AetherNet128, stranger.NextMessage());
        Assert.Null(watch.Hear(new RadioFacts { ServiceData = BleAdParser.Parse(theirs).ServiceData }, rssi: -58));
    }

    [Fact]
    public void BothHalvesFitAStandardAdvertSoAnyPhoneCanSendThem()
    {
        // The whole point: 31 bytes in the advert, 31 in the scan response, and no fee or Bluetooth 5 either side.
        Assert.Equal(31, HelpAdvert.Size(HelpAdvertForm.AetherNet128Pair));
        Assert.Equal(31, HelpAdvert.ScanResponseSize(HelpAdvertForm.AetherNet128Pair));
        Assert.True(HelpAdvert.FitsLegacyAdvert(HelpAdvertForm.AetherNet128Pair));

        // Where the 128-bit form on its own does not, which is why the P30 could not send it.
        Assert.False(HelpAdvert.FitsLegacyAdvert(HelpAdvertForm.AetherNet128));
        Assert.Equal(HelpCodec.Length, HelpAdvert.FirstHalf + HelpAdvert.SecondHalf);
    }

    [Fact]
    public void TheTwoHalvesCarryTheWholeMessageUnderTwoIds()
    {
        var message = new HelpSession(_key, HelpKind.Help, _clock).NextMessage();
        var (advert, rest) = HelpAdvert.BuildPair(message);

        Assert.Equal(31, advert.Length);
        Assert.Equal(31, rest.Length);

        // The advert says what it is; the scan response carries no flags, which is the room the rest needs.
        Assert.Equal(0x02, advert[0]);
        Assert.Equal(0x01, advert[1]);
        Assert.Equal(0x21, rest[1]);

        var first = BleAdParser.Parse(advert).ServiceData;
        var second = BleAdParser.Parse(rest).ServiceData;
        Assert.Equal(Strings.HexOnly(HelpAdvert.ServiceUuid.ToString()), Strings.HexOnly(Assert.Single(first).Uuid));
        Assert.Equal(Strings.HexOnly(HelpAdvert.ServiceUuidRest.ToString()), Strings.HexOnly(Assert.Single(second).Uuid));
        Assert.NotEqual(first[0].Uuid, second[0].Uuid);
        Assert.Equal(HelpAdvert.FirstHalf, Strings.HexToBytes(first[0].DataHex)!.Length);
        Assert.Equal(HelpAdvert.SecondHalf, Strings.HexToBytes(second[0].DataHex)!.Length);

        // Put back together, it is the message that went out, byte for byte.
        Assert.Equal(message, HelpAdvert.TryFind(HelpAdvert.Build(HelpAdvertForm.AetherNet128Pair, message)));
    }

    [Fact]
    public void AGuardianReadsAMessageSentInTwoHalves()
    {
        var watch = new HelpWatch(_clock);
        watch.Watch("person-1", "Thandi", _key);
        var session = new HelpSession(_key, HelpKind.Help, _clock);
        session.UpdatePosition(-26.2041, 28.0473, _clock.NowMs, accuracyM: 7);

        // What a scan hands over: the advert and its scan response, as one reading.
        var (advert, rest) = HelpAdvert.BuildPair(session.NextMessage());
        var facts = new RadioFacts
        {
            ServiceData = [.. BleAdParser.Parse(advert).ServiceData, .. BleAdParser.Parse(rest).ServiceData],
        };

        var seen = watch.Hear(facts, rssi: -58);

        Assert.NotNull(seen);
        Assert.Equal("Thandi", seen.Name);
        Assert.Equal(HelpKind.Help, seen.Kind);
        Assert.Equal(-26.2041, seen.Lat!.Value, 2e-5);
    }

    [Fact]
    public void HalfAMessageIsNotReadAtAll()
    {
        var message = new HelpSession(_key, HelpKind.Help, _clock).NextMessage();
        var (advert, rest) = HelpAdvert.BuildPair(message);

        // A phone that never asked for the scan response holds only the first half. Nothing is reported from half.
        Assert.Null(HelpAdvert.TryFind(advert));
        Assert.Null(HelpAdvert.TryFind(rest));

        // And a first half with somebody else's second half is not a message either.
        var other = new HelpSession(HelpKey.Create(), HelpKind.Help, _clock);
        var (_, theirRest) = HelpAdvert.BuildPair(other.NextMessage());
        var mixed = new RadioFacts
        {
            ServiceData = [.. BleAdParser.Parse(advert).ServiceData, .. BleAdParser.Parse(theirRest).ServiceData],
        };
        var watch = new HelpWatch(_clock);
        watch.Watch("person-1", "Thandi", _key);
        Assert.Null(watch.Hear(mixed, rssi: -58));
    }
}

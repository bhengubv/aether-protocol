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
}

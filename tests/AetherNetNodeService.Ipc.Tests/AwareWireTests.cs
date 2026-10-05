// SPDX-License-Identifier: MIT

using AetherNetNodeService;
using AetherNetNodeService.Ipc;
using Xunit;

namespace AetherNetNodeService.Ipc.Tests;

/// <summary>
/// What Aether Aware hears, crossing the link to a screen. Everything a person is shown has to survive the trip —
/// a field that does not cross is a field the app silently never shows.
/// </summary>
public class AwareWireTests
{
    [Fact]
    public void EverythingAScreenShowsCrossesTheLink()
    {
        var heard = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        var report = new AwareReport
        {
            On = true,
            Things =
            [
                new AwareThing
                {
                    Id = "ble:AA:BB:CC:DD:EE:FF",
                    Name = "Thandi's keys",
                    What = "Apple AirTags",
                    Radio = AwareRadio.Bluetooth,
                    Closeness = AwareCloseness.Here,
                    MovingWithYou = true,
                    FinderTag = true,
                    FirstHeard = heard,
                    LastHeard = heard.AddMinutes(12),
                },
                new AwareThing
                {
                    Id = "wifi:11:22:33:44:55:66",
                    Name = "SPWH_L13",
                    Radio = AwareRadio.WiFi,
                    Closeness = AwareCloseness.Far,
                    Gone = true,
                    FirstHeard = heard,
                    LastHeard = heard.AddMinutes(1),
                },
            ],
            Heard = 13,
            Named = 7,
            MovingWithYou = 1,
            WalkedM = 142.5,
            At = heard.AddMinutes(12),
        };

        var back = NodeWire.DecodeAwareReport(NodeWire.EncodeAwareReport(report));

        Assert.True(back.On);
        Assert.Equal(13, back.Heard);
        Assert.Equal(7, back.Named);
        Assert.Equal(1, back.MovingWithYou);
        Assert.Equal(142.5, back.WalkedM);
        Assert.Equal(report.At, back.At);
        Assert.Equal(2, back.Things.Count);

        var tag = back.Things[0];
        Assert.Equal("ble:AA:BB:CC:DD:EE:FF", tag.Id);
        Assert.Equal("Thandi's keys", tag.Name);
        Assert.Equal("Apple AirTags", tag.What);
        Assert.Equal(AwareRadio.Bluetooth, tag.Radio);
        Assert.Equal(AwareCloseness.Here, tag.Closeness);
        Assert.True(tag.MovingWithYou);
        Assert.True(tag.FinderTag);
        Assert.Equal(heard, tag.FirstHeard);
        Assert.Equal(heard.AddMinutes(12), tag.LastHeard);
        Assert.False(tag.Gone);

        var ap = back.Things[1];
        Assert.Equal(AwareRadio.WiFi, ap.Radio);
        Assert.True(ap.Gone);
        Assert.Null(ap.What);
    }

    [Fact]
    public void WhyItIsNotListeningCrossesTooAndSaysWhetherItCanBePutRight()
    {
        var report = new AwareReport
        {
            On = false,
            Why = "it needs permission to find devices nearby",
            Fixable = true,
        };

        var back = NodeWire.DecodeAwareReport(NodeWire.EncodeAwareReport(report));

        Assert.False(back.On);
        Assert.Equal("it needs permission to find devices nearby", back.Why);
        Assert.True(back.Fixable);
        Assert.Empty(back.Things);
    }

    [Fact]
    public void AServiceWithNoAwareReadsAsNothingHeardRatherThanAFault()
    {
        Assert.Same(AwareReport.None, NodeWire.DecodeAwareReport(null));
        Assert.Same(AwareReport.None, NodeWire.DecodeAwareReport([]));

        // And an older one that sends a report it does not understand the whole of.
        var sparse = NodeWire.DecodeAwareReport(System.Text.Encoding.UTF8.GetBytes("""{"on":true,"heard":3}"""));
        Assert.True(sparse.On);
        Assert.Equal(3, sparse.Heard);
        Assert.Empty(sparse.Things);
        Assert.Null(sparse.Why);
    }

    [Fact]
    public void AThingWeDoNotUnderstandIsShownAsFarAndOverBluetoothRatherThanDropped()
    {
        // A newer service with a radio or a step this one has never heard of. Something is better than nothing, and
        // the quiet end of each is the honest guess.
        var odd = System.Text.Encoding.UTF8.GetBytes(
            """{"on":true,"things":[{"id":"x","name":"Something","radio":42,"closeness":42}]}""");

        var thing = Assert.Single(NodeWire.DecodeAwareReport(odd).Things);

        Assert.Equal("Something", thing.Name);
        Assert.Equal(AwareRadio.Bluetooth, thing.Radio);
        Assert.Equal(AwareCloseness.Far, thing.Closeness);

        // And one with no id at all is not a thing, so it is left out rather than shown as a blank row.
        var nameless = System.Text.Encoding.UTF8.GetBytes("""{"on":true,"things":[{"name":"No id"}]}""");
        Assert.Empty(NodeWire.DecodeAwareReport(nameless).Things);
    }
}

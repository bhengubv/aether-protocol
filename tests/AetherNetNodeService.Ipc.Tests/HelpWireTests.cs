// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNetNodeService.Ipc.Tests;

public class HelpWireTests
{
    private static readonly AetherNetTag Guardian = AetherNetTag.FromPublicKey(new byte[32]);
    private static readonly AetherNetTag Person = AetherNetTag.FromPublicKey([1, .. new byte[31]]);
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AReportCrossesWithEveryFieldItCarries()
    {
        var report = new HelpReport
        {
            Mine = new HelpState
            {
                On = true,
                Kind = HelpKind.Help,
                StartedAt = T0,
                SafeAt = null,
                Lat = -26.2041,
                Lon = 28.0473,
                AccuracyM = 8,
                FixAt = T0.AddSeconds(-3),
                BatteryPercent = 64,
                Nearby = true,
                GuardiansReached = 2,
                Guardians =
                [
                    new HelpGuardian(Guardian, "Sipho"),
                    new HelpGuardian(Person, "Ma", HelpAlert.Quiet),
                ],
                Triggers = new HelpTriggers(HelpTrigger.PowerButton | HelpTrigger.DuressPin, 4, 2_500, 20.5, 2, 1_200, 5),
                Adverts =
                [
                    new HelpAdvertChoice(HelpAdvertForm.Registered16, false, false, "needs a registered Bluetooth ID"),
                    new HelpAdvertChoice(HelpAdvertForm.AetherNet128, true, true),
                ],
                Why = null,
            },
            Watching =
            [
                new HelpWatchCase
                {
                    Person = Person,
                    Name = "Thandi",
                    Kind = HelpKind.Help,
                    Alert = HelpAlert.Loud,
                    FirstHeardAt = T0,
                    LastHeardAt = T0.AddMinutes(2),
                    LastNearbyAt = T0.AddMinutes(1),
                    Lat = -26.21,
                    Lon = 28.05,
                    AccuracyM = 12,
                    FixAt = T0.AddMinutes(1),
                    BatteryPercent = 31,
                    SafeAt = null,
                    Find = HelpFindCue.Closer,
                    Rssi = -62,
                    Trail =
                    [
                        new HelpPoint(T0, -26.2041, 28.0473, 8, Reported: true),
                        new HelpPoint(T0.AddMinutes(1), -26.21, 28.05, null, Reported: false, Rssi: -62),
                    ],
                },
            ],
        };

        var back = NodeWire.DecodeHelpReport(NodeWire.EncodeHelpReport(report));

        Assert.Equal(report.Mine, back.Mine, HelpStateSame);
        var mine = back.Mine;
        Assert.Equal(2, mine.Guardians.Count);
        Assert.Equal("Sipho", mine.Guardians[0].Name);
        Assert.Equal(Guardian.Value, mine.Guardians[0].Tag.Value);
        Assert.Equal(HelpAlert.Quiet, mine.Guardians[1].Alert);
        Assert.Equal(HelpTrigger.PowerButton | HelpTrigger.DuressPin, mine.Triggers.Enabled);
        Assert.Equal(4, mine.Triggers.PowerPresses);
        Assert.Equal(20.5, mine.Triggers.ShakeThreshold);
        Assert.Equal(5, mine.Triggers.HoldSeconds);
        Assert.False(mine.Adverts[0].Available);
        Assert.Equal("needs a registered Bluetooth ID", mine.Adverts[0].Why);
        Assert.True(mine.Adverts[1].Chosen);

        var c = Assert.Single(back.Watching);
        Assert.Equal(Person.Value, c.Person.Value);
        Assert.Equal("Thandi", c.Name);
        Assert.Equal(HelpKind.Help, c.Kind);
        Assert.Equal(HelpFindCue.Closer, c.Find);
        Assert.Equal(-62, c.Rssi);
        Assert.Equal(31, c.BatteryPercent);
        Assert.Equal(T0.AddMinutes(1), c.LastNearbyAt);
        Assert.Equal(2, c.Trail.Count);
        Assert.True(c.Trail[0].Reported);
        Assert.Equal(8, c.Trail[0].AccuracyM);
        Assert.False(c.Trail[1].Reported);
        Assert.Equal(-62, c.Trail[1].Rssi);
        Assert.False(c.IsSafe);
    }

    [Fact]
    public void NothingAtAllReadsAsNoHelp()
    {
        Assert.Equal(HelpReport.None, NodeWire.DecodeHelpReport([]), HelpReportSame);
        Assert.Equal(HelpReport.None, NodeWire.DecodeHelpReport(null), HelpReportSame);
        var empty = NodeWire.DecodeHelpReport(NodeWire.EncodeHelpReport(HelpReport.None));
        Assert.False(empty.Mine.On);
        Assert.Equal(HelpKind.Safe, empty.Mine.Kind);
        Assert.Empty(empty.Mine.Guardians);
        Assert.Empty(empty.Watching);
        // A service that never heard of Quiet help sends nothing, and the app draws the defaults.
        Assert.Equal(new HelpTriggers(), empty.Mine.Triggers);
    }

    [Fact]
    public void TheKindCrossesAsOneByteAndAnUnknownOneStartsNothing()
    {
        foreach (var kind in new[] { HelpKind.Help, HelpKind.Walk, HelpKind.Safe })
        {
            Assert.Equal(kind, NodeWire.DecodeHelpKind(NodeWire.EncodeHelpKind(kind)));
        }
        Assert.Equal(HelpKind.Safe, NodeWire.DecodeHelpKind([99]));
        Assert.Equal(HelpKind.Safe, NodeWire.DecodeHelpKind([]));
        Assert.Equal(HelpKind.Safe, NodeWire.DecodeHelpKind(null));
    }

    [Fact]
    public void TheGuardiansCrossAndOneWhoseTagIsNonsenseIsDropped()
    {
        IReadOnlyList<HelpGuardian> chosen =
        [
            new HelpGuardian(Guardian, "Sipho"),
            new HelpGuardian(Person, "Ma", HelpAlert.Quiet),
        ];
        var back = NodeWire.DecodeHelpGuardians(NodeWire.EncodeHelpGuardians(chosen));
        Assert.Equal(2, back.Count);
        Assert.Equal("Ma", back[1].Name);
        Assert.Equal(HelpAlert.Quiet, back[1].Alert);

        var nonsense = System.Text.Encoding.UTF8.GetBytes("""[{"tag":"not-a-tag","name":"X","alert":0}]""");
        Assert.Empty(NodeWire.DecodeHelpGuardians(nonsense));
        Assert.Empty(NodeWire.DecodeHelpGuardians([]));
        Assert.Empty(NodeWire.DecodeHelpGuardians(null));
    }

    [Fact]
    public void TheOptionsCrossAndAnUnknownContainerFallsBackToOurOwn()
    {
        var triggers = new HelpTriggers(HelpTrigger.Shake, 3, 1_000, 30.0, 4, 2_000, 10);
        var (back, advert) = NodeWire.DecodeHelpOptions(NodeWire.EncodeHelpOptions(triggers, HelpAdvertForm.Registered16));
        Assert.Equal(triggers, back);
        Assert.Equal(HelpAdvertForm.Registered16, advert);

        var odd = System.Text.Encoding.UTF8.GetBytes("""{"advert":42}""");
        var (defaults, fallback) = NodeWire.DecodeHelpOptions(odd);

        // Ours, in the two halves every phone can send and hear — the widest reach that costs nothing.
        Assert.Equal(HelpAdvertForm.AetherNet128Pair, fallback);
        Assert.Equal(new HelpTriggers(), defaults);
    }

    [Fact]
    public void AnUnknownAlertOrCueReadsAsTheQuietEnd()
    {
        var report = System.Text.Encoding.UTF8.GetBytes(
            """"
            {"mine":{"on":true,"kind":77,"guardians":[{"tag":"","name":"X","alert":9}]},
             "watching":[{"person":"","name":"Y","kind":0,"alert":9,"find":99}]}
            """");
        var back = NodeWire.DecodeHelpReport(report);
        Assert.Equal(HelpKind.Safe, back.Mine.Kind);
        Assert.Equal(HelpAlert.Loud, Assert.Single(back.Mine.Guardians).Alert);
        var c = Assert.Single(back.Watching);
        Assert.Equal(HelpAlert.Loud, c.Alert);
        Assert.Equal(HelpFindCue.Waiting, c.Find);
    }

    private static readonly IEqualityComparer<HelpState> HelpStateSame =
        new Same<HelpState>((a, b) =>
            a.On == b.On && a.Kind == b.Kind && a.StartedAt == b.StartedAt && a.SafeAt == b.SafeAt &&
            a.Lat == b.Lat && a.Lon == b.Lon && a.AccuracyM == b.AccuracyM && a.FixAt == b.FixAt &&
            a.BatteryPercent == b.BatteryPercent && a.Nearby == b.Nearby &&
            a.GuardiansReached == b.GuardiansReached && a.Triggers == b.Triggers && a.Why == b.Why &&
            a.Guardians.SequenceEqual(b.Guardians) && a.Adverts.SequenceEqual(b.Adverts));

    private static readonly IEqualityComparer<HelpReport> HelpReportSame =
        new Same<HelpReport>((a, b) => HelpStateSame.Equals(a.Mine, b.Mine) && a.Watching.Count == b.Watching.Count);

    /// <summary>Records compare collections by reference, so the fields are compared one by one.</summary>
    private sealed class Same<T>(Func<T, T, bool> same) : IEqualityComparer<T>
    {
        public bool Equals(T? a, T? b) => a is null || b is null ? a is null && b is null : same(a, b);

        public int GetHashCode(T value) => 0;
    }

    [Fact]
    public void TheReasonThePhonesNearbyCannotHearItCrossesTheLink()
    {
        var report = new HelpReport
        {
            Mine = new HelpState
            {
                NearbyWhy = "needs permission to find devices nearby",
                NearbyFixable = true,
            },
        };

        var back = NodeWire.DecodeHelpReport(NodeWire.EncodeHelpReport(report));

        Assert.Equal("needs permission to find devices nearby", back.Mine.NearbyWhy);
        Assert.True(back.Mine.NearbyFixable);

        // And an older service, which says nothing about it, reads as nothing to say rather than as a fault.
        var older = NodeWire.DecodeHelpReport(System.Text.Encoding.UTF8.GetBytes("""{"mine":{"on":false}}"""));
        Assert.Null(older.Mine.NearbyWhy);
        Assert.False(older.Mine.NearbyFixable);
    }
}

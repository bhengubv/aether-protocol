// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;
using Aware = AetherNet.Aware;

namespace AetherNetNodeService.Help.Tests;

public sealed class QuietHelpTests : IDisposable
{
    private const double Lat = -26.2041;
    private const double Lon = 28.0473;

    private static readonly AetherNetTag Thandi = AetherNetTag.FromPublicKey([1, .. new byte[31]]);
    private static readonly AetherNetTag Sipho = AetherNetTag.FromPublicKey([2, .. new byte[31]]);
    private static readonly AetherNetTag Ma = AetherNetTag.FromPublicKey([3, .. new byte[31]]);
    private static readonly AetherNetTag Stranger = AetherNetTag.FromPublicKey([4, .. new byte[31]]);

    private readonly ManualClock _clock = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aether-help-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _open = [];

    [Fact]
    public async Task WithNobodyChosenItRefusesAndSaysWhy()
    {
        var (help, _, _) = Node(Thandi);

        Assert.False(help.Start(HelpKind.Help));
        var mine = help.Current.Mine;
        Assert.False(mine.On);
        Assert.Contains("nobody chosen", mine.Why, StringComparison.OrdinalIgnoreCase);
        await help.TickAsync();   // nothing to send, and nothing thrown
        Assert.Equal(HelpKind.Safe, help.Current.Mine.Kind);
    }

    [Fact]
    public async Task ThePersonAsksAndItGoesOutBothWays()
    {
        var (help, messaging, radio) = Node(Thandi);
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho"), new HelpGuardian(Ma, "Ma", HelpAlert.Quiet)]);
        help.UpdatePosition(Lat, Lon, _clock.NowMs, accuracyM: 8);
        help.UpdateBattery(64);

        Assert.True(help.Start(HelpKind.Help));
        await help.TickAsync();

        var mine = help.Current.Mine;
        Assert.True(mine.On);
        Assert.Equal(HelpKind.Help, mine.Kind);
        Assert.Equal(2, mine.GuardiansReached);
        Assert.True(mine.Nearby);
        Assert.Equal(64, mine.BatteryPercent);
        Assert.Equal(Lat, mine.Lat!.Value, 1e-9);
        Assert.Equal(8, mine.AccuracyM);
        Assert.Null(mine.Why);

        // On the air: the message itself, which the radio wraps in its own advert — and that advert is readable.
        var (onAir, form, _) = radio.Adverts[^1];
        Assert.Equal(HelpAdvertForm.AetherNet128, form);
        Assert.Equal(Aware.HelpCodec.Length, onAir.Length);
        Assert.Contains(messaging.Sent, s => s.Payload.SequenceEqual(onAir));
        Assert.Equal(onAir, Aware.HelpAdvert.TryFind(radio.LastAdvert()));

        // And over the mesh: the key to each guardian, then the message to each guardian.
        Assert.Contains(messaging.Sent, s => s.To.Value == Sipho.Value && s.Payload[0] == QuietHelp.KeyShareMarker);
        Assert.Contains(messaging.Sent, s => s.To.Value == Ma.Value && s.Payload[0] == QuietHelp.KeyShareMarker);
        Assert.Contains(messaging.Sent, s => s.To.Value == Sipho.Value && s.Payload.Length == Aware.HelpCodec.Length);
        Assert.Contains(messaging.Sent, s => s.To.Value == Ma.Value && s.Payload.Length == Aware.HelpCodec.Length);
    }

    [Fact]
    public async Task AGuardianSeesWhoNeedsHelpAndWhere()
    {
        var bus = new Bus();
        var (her, _, _) = Node(Thandi, bus);
        var (his, _, _) = Node(Sipho, bus);

        her.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        her.UpdatePosition(Lat, Lon, _clock.NowMs, accuracyM: 6);
        her.UpdateBattery(40);
        Assert.True(her.Start(HelpKind.Help));
        await her.TickAsync();

        var seen = Assert.Single(his.Current.Watching);
        Assert.Equal(Thandi.Value, seen.Person.Value);
        Assert.Equal("Sipho", seen.Name);   // the name she gave the guardian travels with the key
        Assert.Equal(HelpKind.Help, seen.Kind);
        Assert.Equal(HelpAlert.Loud, seen.Alert);
        Assert.Equal(Lat, seen.Lat!.Value, 2e-5);
        Assert.Equal(Lon, seen.Lon!.Value, 2e-5);
        Assert.Equal(40, seen.BatteryPercent);
        Assert.False(seen.IsSafe);
        var point = Assert.Single(seen.Trail);
        Assert.True(point.Reported);

        // She walks on; his phone follows the trail.
        _clock.Advance(TimeSpan.FromSeconds(30));
        her.UpdatePosition(Lat + 0.001, Lon, _clock.NowMs, accuracyM: 6);
        await her.TickAsync();
        Assert.Equal(2, Assert.Single(his.Current.Watching).Trail.Count);

        // She is safe, and his phone says so.
        her.MarkSafe();
        await her.TickAsync();
        Assert.True(Assert.Single(his.Current.Watching).IsSafe);
    }

    [Fact]
    public async Task OnlyTheGuardiansSheChoseCanRead()
    {
        var bus = new Bus();
        var (her, _, _) = Node(Thandi, bus);
        var (his, _, _) = Node(Sipho, bus);
        var (theirs, _, _) = Node(Stranger, bus);

        her.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        her.Start(HelpKind.Help);
        await her.TickAsync();

        Assert.Single(his.Current.Watching);
        Assert.Empty(theirs.Current.Watching);

        // And a stranger's own message is nothing to his phone either.
        var mine = Aware.HelpCodec.Encode(Aware.HelpKey.Create(), new Aware.HelpMessage { Kind = Aware.HelpKind.Help }, _clock.NowMs);
        his.Heard(new Aware.RadioFacts { ServiceData = [new(Aware.HelpAdvert.ServiceUuid.ToString(), Convert.ToHexString(mine))] }, rssi: -50);
        Assert.Single(his.Current.Watching);
    }

    [Fact]
    public async Task AGuardianStandingNearHearsItOffTheAirWithNoNetworkAtAll()
    {
        var bus = new Bus();
        var (her, herMessaging, herRadio) = Node(Thandi, bus);
        var (his, _, hisRadio) = Node(Sipho, bus);

        // Her key reaches him over the mesh, as it does when she chooses him.
        her.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        her.Start(HelpKind.Help);
        await her.TickAsync();
        Assert.Single(his.Current.Watching);

        // Now the mesh cannot carry her message at all — his phone only hears the air.
        herMessaging.Accept = false;
        _clock.Advance(TimeSpan.FromSeconds(10));
        her.UpdatePosition(Lat, Lon, _clock.NowMs, accuracyM: 5);
        await her.TickAsync();
        hisRadio.Hear(herRadio.LastAdvert(), rssi: -55);

        var seen = Assert.Single(his.Current.Watching);
        Assert.Equal(Lat, seen.Lat!.Value, 2e-5);
        Assert.NotNull(seen.LastNearbyAt);
        Assert.Equal(-55, seen.Rssi);

        // Louder each time: Find it leads him in.
        foreach (var rssi in new[] { -50, -45, -40 })
        {
            _clock.Advance(TimeSpan.FromMilliseconds(900));
            hisRadio.Hear(herRadio.LastAdvert(), rssi);
        }

        Assert.Equal(HelpFindCue.VeryClose, Assert.Single(his.Current.Watching).Find);
    }

    [Fact]
    public async Task WithNoPositionTheGuardianKeepsWhereItHeardThem()
    {
        var bus = new Bus();
        var (her, _, herRadio) = Node(Thandi, bus);
        var (his, _, hisRadio) = Node(Sipho, bus);
        her.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        her.Start(HelpKind.Help);
        await her.TickAsync();

        hisRadio.Hear(herRadio.LastAdvert(), rssi: -60, whereIAm: new Aware.GpsSample(_clock.NowMs, Lat, Lon));

        var seen = Assert.Single(his.Current.Watching);
        Assert.Null(seen.Lat);
        var point = Assert.Single(seen.Trail);
        Assert.False(point.Reported);
        Assert.Equal(Lat, point.Lat);
        Assert.Equal(-60, point.Rssi);
    }

    [Fact]
    public async Task OnlyMarkingSafeEndsItAndTheMinuteIsHonoured()
    {
        var (help, _, radio) = Node(Thandi);
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        help.Start(HelpKind.Help);
        await help.TickAsync();

        help.MarkSafe();
        await help.TickAsync();
        Assert.Equal(HelpKind.Safe, help.Current.Mine.Kind);
        Assert.True(help.Current.Mine.On);        // still going out, so guardians nearby hear it
        Assert.Equal(0, radio.Stops);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await help.TickAsync();
        var mine = help.Current.Mine;
        Assert.False(mine.On);
        Assert.Equal(1, radio.Stops);
        Assert.False(mine.Nearby);
        Assert.Equal(0, mine.GuardiansReached);
    }

    [Fact]
    public async Task AMeshThatOnlyHoldsItStillCounts()
    {
        var (help, messaging, _) = Node(Thandi);
        messaging.HoldOnly = true;
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        help.Start(HelpKind.Help);
        await help.TickAsync();
        Assert.Equal(1, help.Current.Mine.GuardiansReached);

        messaging.Accept = false;
        await help.TickAsync();
        Assert.Equal(0, help.Current.Mine.GuardiansReached);
        Assert.True(help.Current.Mine.On);   // refused by the mesh, but still on the air and still running
    }

    [Fact]
    public async Task WithNoRadioItStillReachesTheGuardians()
    {
        var bus = new Bus();
        var messaging = new FakeMessaging(Thandi, bus);
        var help = Keep(new QuietHelp(Store(), messaging, new NoHelpRadio(), _clock));
        var (his, _, _) = Node(Sipho, bus);
        bus.Join(Thandi, messaging);

        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        Assert.True(help.Start(HelpKind.Help));
        await help.TickAsync();

        var mine = help.Current.Mine;
        Assert.True(mine.On);
        Assert.False(mine.Nearby);
        Assert.Equal(1, mine.GuardiansReached);
        Assert.Single(his.Current.Watching);
        Assert.All(mine.Adverts, a => Assert.False(a.Available));
        Assert.All(mine.Adverts, a => Assert.False(string.IsNullOrWhiteSpace(a.Why)));
    }

    [Fact]
    public void TheStandardAdvertSaysItNeedsARegisteredId()
    {
        var radio = new FakeRadio();
        radio.Able.Add(HelpAdvertForm.Registered16);
        var help = Keep(new QuietHelp(Store(), new FakeMessaging(Thandi), radio, _clock));

        var adverts = help.Current.Mine.Adverts;
        var standard = adverts.Single(a => a.Form == HelpAdvertForm.Registered16);
        Assert.False(standard.Available);
        Assert.Contains("registered", standard.Why, StringComparison.OrdinalIgnoreCase);
        var ours = adverts.Single(a => a.Form == HelpAdvertForm.AetherNet128);
        Assert.True(ours.Available);
        Assert.True(ours.Chosen);
        Assert.Null(ours.Why);
    }

    [Fact]
    public async Task WithARegisteredIdTheStandardAdvertIsUsed()
    {
        var radio = new FakeRadio();
        radio.Able.Add(HelpAdvertForm.Registered16);
        var help = Keep(new QuietHelp(Store(), new FakeMessaging(Thandi), radio, _clock, registeredAdvertId: 0xFD6F));
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        help.SetOptions(new HelpTriggers(), HelpAdvertForm.Registered16);

        Assert.True(help.Current.Mine.Adverts.Single(a => a.Form == HelpAdvertForm.Registered16).Available);
        help.Start(HelpKind.Help);
        await help.TickAsync();

        var (message, form, registeredId) = radio.Adverts[^1];
        Assert.Equal(HelpAdvertForm.Registered16, form);
        Assert.Equal<ushort?>(0xFD6F, registeredId);
        Assert.Equal(30, radio.LastAdvert().Length);   // the standard advert, which every phone can send
        Assert.Equal(message, Aware.HelpAdvert.TryFind(radio.LastAdvert(), registeredId: 0xFD6F));
    }

    [Fact]
    public async Task AContainerThisDeviceCannotUseIsNotTaken()
    {
        var (help, _, radio) = Node(Thandi);   // can only do AetherNet128
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        help.SetOptions(new HelpTriggers(), HelpAdvertForm.Registered16);

        Assert.True(help.Current.Mine.Adverts.Single(a => a.Form == HelpAdvertForm.AetherNet128).Chosen);
        help.Start(HelpKind.Help);
        await help.TickAsync();
        Assert.Equal(HelpAdvertForm.AetherNet128, radio.Adverts[^1].Form);
    }

    [Fact]
    public void TheTriggersThePersonChoseAreKeptAndGivenBack()
    {
        var (help, _, _) = Node(Thandi);
        var triggers = new HelpTriggers(HelpTrigger.PowerButton | HelpTrigger.DuressPin, 4, 2_500, 20.5, 2, 1_200, 5);
        help.SetOptions(triggers, HelpAdvertForm.AetherNet128);

        Assert.Equal(triggers, help.Current.Mine.Triggers);
        Assert.True(help.Current.Mine.Triggers.On(HelpTrigger.DuressPin));
        Assert.False(help.Current.Mine.Triggers.On(HelpTrigger.Shake));
    }

    [Fact]
    public async Task ChangingWhoMayReadMintsANewKeyAndARenameDoesNot()
    {
        var (help, messaging, _) = Node(Thandi);

        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        var firstShare = messaging.Sent.Count(s => s.Payload[0] == QuietHelp.KeyShareMarker);
        Assert.Equal(1, firstShare);
        var firstKey = messaging.Sent.Last(s => s.Payload[0] == QuietHelp.KeyShareMarker).Payload[1..33];

        // Renaming them is not a change of who may read: the same key stays.
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho M")]);
        Assert.Equal(firstShare, messaging.Sent.Count(s => s.Payload[0] == QuietHelp.KeyShareMarker));

        // Adding somebody is: a new key, handed to both of them.
        help.SetGuardians([new HelpGuardian(Sipho, "Sipho M"), new HelpGuardian(Ma, "Ma")]);
        var shares = messaging.Sent.Where(s => s.Payload[0] == QuietHelp.KeyShareMarker).ToList();
        Assert.Equal(3, shares.Count);
        var newKey = shares[^1].Payload[1..33];
        Assert.NotEqual(firstKey, newKey);

        help.Start(HelpKind.Help);
        await help.TickAsync();
        Assert.Equal(2, help.Current.Mine.GuardiansReached);
    }

    [Fact]
    public async Task AGuardianTakenOffCannotReadWhatComesAfter()
    {
        var bus = new Bus();
        var (her, _, _) = Node(Thandi, bus);
        var (his, _, _) = Node(Sipho, bus);

        her.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        her.Start(HelpKind.Help);
        await her.TickAsync();

        // That one ends, and his phone sees it end.
        her.MarkSafe();
        await her.TickAsync();
        _clock.Advance(TimeSpan.FromSeconds(61));
        await her.TickAsync();
        var ended = Assert.Single(his.Current.Watching);
        Assert.True(ended.IsSafe);
        var lastHeard = ended.LastHeardAt;

        // She takes him off and asks somebody else. His phone is handed no new key.
        her.SetGuardians([new HelpGuardian(Ma, "Ma")]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(her.Start(HelpKind.Help));
        await her.TickAsync();

        // Nothing of the new session reaches him: his phone still shows only the one that ended.
        var stale = Assert.Single(his.Current.Watching);
        Assert.True(stale.IsSafe);
        Assert.Equal(lastHeard, stale.LastHeardAt);
    }

    [Fact]
    public async Task ItWillOnlyWatchOverSoMany()
    {
        var bus = new Bus();
        var (mine, _, _) = Node(Sipho, bus);

        for (var i = 0; i < QuietHelp.MostWatched + 5; i++)
        {
            var from = AetherNetTag.FromPublicKey([(byte)(10 + i), (byte)i, .. new byte[30]]);
            var sender = new FakeMessaging(from, bus);
            var key = Aware.HelpKey.Create();

            var share = new byte[1 + Aware.HelpKey.Length + 1 + 1];
            share[0] = QuietHelp.KeyShareMarker;
            key.ToBytes().CopyTo(share, 1);
            share[^1] = (byte)'x';
            await sender.SendAsync(Sipho, share, Guid.NewGuid());

            var asking = Aware.HelpCodec.Encode(key, new Aware.HelpMessage { Kind = Aware.HelpKind.Help }, _clock.NowMs);
            await sender.SendAsync(Sipho, asking, Guid.NewGuid());
        }

        // The ones it took, it shows; the rest it never took on at all.
        Assert.Equal(QuietHelp.MostWatched, mine.Current.Watching.Count);
    }

    [Fact]
    public void WhatThePersonChoseSurvivesARestart()
    {
        var dir = Store();
        var first = Keep(new QuietHelp(dir, new FakeMessaging(Thandi), new FakeRadio(), _clock));
        first.SetGuardians([new HelpGuardian(Sipho, "Sipho", HelpAlert.Quiet)]);
        first.SetOptions(new HelpTriggers(HelpTrigger.Shake, PowerPresses: 3), HelpAdvertForm.AetherNet128);
        var key = dir.Key.ToBytes();

        var again = Keep(new QuietHelp(Store(), new FakeMessaging(Thandi), new FakeRadio(), _clock));
        var mine = again.Current.Mine;
        var guardian = Assert.Single(mine.Guardians);
        Assert.Equal(Sipho.Value, guardian.Tag.Value);
        Assert.Equal("Sipho", guardian.Name);
        Assert.Equal(HelpAlert.Quiet, guardian.Alert);
        Assert.Equal(HelpTrigger.Shake, mine.Triggers.Enabled);
        Assert.Equal(3, mine.Triggers.PowerPresses);
        Assert.Equal(key, Store().Key.ToBytes());
    }

    [Fact]
    public async Task AnAppIsToldWheneverAnythingChanges()
    {
        var (help, _, _) = Node(Thandi);
        var changes = 0;
        help.Changed += () => changes++;

        help.SetGuardians([new HelpGuardian(Sipho, "Sipho")]);
        Assert.True(changes > 0);

        var before = changes;
        help.Start(HelpKind.Walk);
        await help.TickAsync();
        Assert.True(changes > before);
    }

    [Fact]
    public void AMessageThatIsNothingToQuietHelpIsLeftAlone()
    {
        var (help, messaging, _) = Node(Thandi);
        messaging.Receive(Stranger, []);
        messaging.Receive(Stranger, [0x09, 0x09, 0x09]);
        messaging.Receive(Stranger, new byte[Aware.HelpCodec.Length]);
        messaging.Receive(Stranger, [QuietHelp.KeyShareMarker, 1, 2, 3]);   // too short to be a key

        Assert.Empty(help.Current.Watching);
    }

    private HelpStore Store() => new(_dir);

    private (QuietHelp Help, FakeMessaging Messaging, FakeRadio Radio) Node(AetherNetTag me, Bus? bus = null)
    {
        var messaging = new FakeMessaging(me, bus);
        var radio = new FakeRadio();
        var help = Keep(new QuietHelp(new HelpStore(Path.Combine(_dir, me.Value!)), messaging, radio, _clock));
        bus?.Join(me, messaging);
        return (help, messaging, radio);
    }

    private T Keep<T>(T disposable) where T : IDisposable
    {
        _open.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        foreach (var open in _open)
        {
            open.Dispose();
        }

        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory the system will clean up anyway.
        }
    }
}

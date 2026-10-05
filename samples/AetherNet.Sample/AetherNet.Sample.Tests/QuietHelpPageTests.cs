// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Pages;
using AetherNet.Sample.Shared.Services;
using AetherNetNodeService;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Quiet help's screen. A thin client: everything it shows comes from the node's one report, and every tap is one
/// call back to the node — so these prove the screen says what the node said, and asks for what the person tapped.
/// </summary>
public class QuietHelpPageTests : IDisposable
{
    private static readonly AetherNetTag Sipho = AetherNetTag.FromPublicKey([2, .. new byte[31]]);
    private static readonly AetherNetTag Thandi = AetherNetTag.FromPublicKey([1, .. new byte[31]]);

    private readonly TestContext _ctx = new();
    private readonly FakeNode _node = new();
    private readonly string _db = Path.Combine(Path.GetTempPath(), "aether-qhelp-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly AetherStore _store;

    public QuietHelpPageTests()
    {
        _store = new AetherStore(_db);
        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton<IAetherNodeClient>(_node);
        _ctx.Services.AddSingleton(sp => new QuietHelpService(sp.GetService<IAetherNodeClient>()));
    }

    [Fact]
    public void ItOffersBothWaysAndWillNotStartWithNobodyChosen()
    {
        _node.Report = new HelpReport { Mine = new HelpState { Why = "nobody chosen to ask yet" } };
        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("Ask for help, quietly", page.Markup);
        Assert.Contains("Walk with me", page.Markup);
        Assert.Contains("nobody chosen to ask yet", page.Markup);
        Assert.All(page.FindAll("button.qhelp-ask"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Fact]
    public void WithSomebodyChosenTheTapAsksTheNode()
    {
        _node.Report = Running(on: false);
        var page = _ctx.RenderComponent<QuietHelp>();

        page.FindAll("button.about").First(b => b.TextContent.Contains("Ask for help")).Click();

        Assert.Equal(HelpKind.Help, _node.Started);
    }

    [Fact]
    public void ARunningSessionShowsItsReachAndTheWayToStopIt()
    {
        _node.Report = Running(on: true, reached: 2, guardians: 3, nearby: true, lat: -26.2041, accuracy: 9);
        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("You asked for help", page.Markup);
        Assert.Contains("2 of 3 taken by the mesh", page.Markup);
        Assert.Contains("on the air for phones near you", page.Markup);
        Assert.Contains("to about 9 m", page.Markup);

        page.Find("button.qhelp-safe").Click();
        Assert.True(_node.MarkedSafe);
    }

    [Fact]
    public void WithNoPositionItSaysSoRatherThanLookingEmpty()
    {
        _node.Report = Running(on: true, reached: 1, guardians: 1);
        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("No position yet", page.Markup);
    }

    [Fact]
    public void SomebodyAskingThisPersonForHelpIsShownWithHowCloseTheySound()
    {
        _node.Report = new HelpReport
        {
            Mine = new HelpState(),
            Watching =
            [
                new HelpWatchCase
                {
                    Person = Thandi,
                    Name = "Thandi",
                    Kind = HelpKind.Help,
                    FirstHeardAt = DateTimeOffset.UtcNow,
                    LastHeardAt = DateTimeOffset.UtcNow,
                    LastNearbyAt = DateTimeOffset.UtcNow,
                    Lat = -26.21,
                    Lon = 28.05,
                    AccuracyM = 12,
                    BatteryPercent = 31,
                    Find = HelpFindCue.Closer,
                    Trail = [new HelpPoint(DateTimeOffset.UtcNow, -26.21, 28.05, 12, Reported: true)],
                },
            ],
        };
        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("Asking you for help", page.Markup);
        Assert.Contains("Thandi — needs help", page.Markup);
        Assert.Contains("to about 12 m", page.Markup);
        Assert.Contains("battery 31%", page.Markup);
        Assert.Contains("closer", page.Markup);
    }

    [Fact]
    public void AContactGoesOffThenLoudThenQuietOnOneTapEach()
    {
        _store.UpsertContact(Sipho.Value!, null, byMe: true, byThem: true, via: "test", displayName: "Sipho");
        _node.Report = new HelpReport { Mine = new HelpState() };
        var page = _ctx.RenderComponent<QuietHelp>();

        var row = page.FindAll("button.about").First(b => b.TextContent.Contains("Sipho"));
        Assert.Contains("off", row.TextContent);
        row.Click();

        var chosen = Assert.Single(_node.Guardians);
        Assert.Equal(Sipho.Value, chosen.Tag.Value);
        Assert.Equal("Sipho", chosen.Name);
        Assert.Equal(HelpAlert.Loud, chosen.Alert);

        // The next tap lets them have it quietly, and the one after turns them off again.
        page.FindAll("button.about").First(b => b.TextContent.Contains("Sipho")).Click();
        Assert.Equal(HelpAlert.Quiet, Assert.Single(_node.Guardians).Alert);

        page.FindAll("button.about").First(b => b.TextContent.Contains("Sipho")).Click();
        Assert.Empty(_node.Guardians);
    }

    [Fact]
    public void EveryTriggerSaysWhatOnAndOffMeanAndCanBeTurnedOff()
    {
        _node.Report = new HelpReport { Mine = new HelpState() };
        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("The power button, five times", page.Markup);
        Assert.Contains("Works with the screen locked", page.Markup);
        Assert.Contains("Shaking the phone", page.Markup);
        Assert.Contains("A second PIN", page.Markup);
        Assert.Contains("Only your own PIN opens Aether", page.Markup);   // off by default, so the off words show

        page.FindAll("button.about").First(b => b.TextContent.Contains("Shaking the phone")).Click();
        Assert.NotNull(_node.Triggers);
        Assert.False(_node.Triggers!.On(HelpTrigger.Shake));
        Assert.True(_node.Triggers.On(HelpTrigger.PowerButton));
    }

    [Fact]
    public void AnAdvertThisPhoneCannotSendSaysWhyAndCannotBeChosen()
    {
        _node.Report = new HelpReport
        {
            Mine = new HelpState
            {
                Adverts =
                [
                    new HelpAdvertChoice(HelpAdvertForm.Registered16, false, false, "needs a 16-bit Bluetooth service ID registered to us"),
                    new HelpAdvertChoice(HelpAdvertForm.AetherNet128, true, true),
                ],
            },
        };
        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("needs a 16-bit Bluetooth service ID registered to us", page.Markup);
        Assert.Contains("in use", page.Markup);
        var standard = page.FindAll("button.about").First(b => b.TextContent.Contains("every phone can hear it"));
        Assert.True(standard.HasAttribute("disabled"));
        standard.Click();
        Assert.Null(_node.Advert);   // not chosen, because this phone cannot send it
    }

    [Fact]
    public void WithNoNodeTheScreenSaysItNeedsTheService()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(_store);
        ctx.Services.AddSingleton(_ => new QuietHelpService(node: null));

        var page = ctx.RenderComponent<QuietHelp>();
        Assert.Contains("needs AetherNetService", page.Markup);
        Assert.All(page.FindAll("button.qhelp-ask"), b => Assert.True(b.HasAttribute("disabled")));
    }

    private static HelpReport Running(
        bool on, int reached = 0, int guardians = 1, bool nearby = false, double? lat = null, int? accuracy = null)
    {
        var chosen = new List<HelpGuardian>();
        for (var i = 0; i < guardians; i++)
        {
            chosen.Add(new HelpGuardian(AetherNetTag.FromPublicKey([(byte)(20 + i), .. new byte[31]]), "G" + i));
        }

        return new HelpReport
        {
            Mine = new HelpState
            {
                On = on,
                Kind = on ? HelpKind.Help : HelpKind.Safe,
                StartedAt = on ? DateTimeOffset.UtcNow : null,
                GuardiansReached = reached,
                Nearby = nearby,
                Lat = lat,
                Lon = lat is null ? null : 28.0473,
                AccuracyM = accuracy,
                Guardians = chosen,
            },
        };
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _store.Dispose();
        try
        {
            if (File.Exists(_db))
            {
                File.Delete(_db);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A node that answers Quiet help and remembers what the screen asked of it.</summary>
    private sealed class FakeNode : IAetherNodeClient
    {
        public HelpReport Report { get; set; } = HelpReport.None;

        public HelpKind? Started { get; private set; }

        public bool MarkedSafe { get; private set; }

        public IReadOnlyList<HelpGuardian> Guardians { get; private set; } = [];

        public HelpTriggers? Triggers { get; private set; }

        public HelpAdvertForm? Advert { get; private set; }

        public Task<HelpReport> GetHelpAsync(CancellationToken cancellationToken = default) => Task.FromResult(Report);

        public Task<bool> StartHelpAsync(HelpKind kind, CancellationToken cancellationToken = default)
        {
            Started = kind;
            return Task.FromResult(true);
        }

        public Task MarkSafeAsync(CancellationToken cancellationToken = default)
        {
            MarkedSafe = true;
            return Task.CompletedTask;
        }

        public Task SetHelpGuardiansAsync(IReadOnlyList<HelpGuardian> guardians, CancellationToken cancellationToken = default)
        {
            Guardians = guardians;
            Report = Report with { Mine = Report.Mine with { Guardians = guardians } };
            return Task.CompletedTask;
        }

        public Task SetHelpOptionsAsync(HelpTriggers triggers, HelpAdvertForm advert, CancellationToken cancellationToken = default)
        {
            Triggers = triggers;
            Advert = advert;
            Report = Report with { Mine = Report.Mine with { Triggers = triggers } };
            return Task.CompletedTask;
        }

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => Task.FromResult(Thandi);

        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new byte[32]);

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[64]);

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(OutboundResult.Queued);

        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InboundMessage>>([]);

        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(NodeLinkStatus.Offline);

        /// <summary>How many times it refuses to be listened to before it allows it. A service not up yet.</summary>
        public int RefuseListening { get; set; }

        public int Subscriptions { get; private set; }

        public IDisposable Subscribe(IAetherNodeEvents listener)
        {
            if (RefuseListening > 0)
            {
                RefuseListening--;
                throw new InvalidOperationException("the service is not answering yet");
            }

            Subscriptions++;
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public void TheScreenSaysWhyPhonesNearbyCannotHearAndOffersTheWayThere()
    {
        var settings = new FakeServiceSettings();
        _ctx.Services.AddSingleton<AetherNetNodeService.Client.IAetherNetServiceSettings>(settings);
        _node.Report = new HelpReport
        {
            Mine = new HelpState
            {
                Guardians = [new HelpGuardian(Sipho, "Sipho")],
                NearbyWhy = "needs permission to find devices nearby",
                NearbyFixable = true,
            },
        };

        var page = _ctx.RenderComponent<QuietHelp>();

        // The reason, on this screen, not buried in Settings — and what it costs, in plain words.
        Assert.Contains("Phones next to you cannot hear you yet", page.Markup);
        Assert.Contains("needs permission to find devices nearby", page.Markup);
        Assert.Contains("still works", page.Markup);

        // One tap opens AetherNetService's own page, because only that page can grant its permissions.
        page.FindAll("button.about").First(b => b.TextContent.Contains("Put it right")).Click();
        Assert.Equal(PermissionPage.AppInfo, settings.Opened);
    }

    [Fact]
    public void TheScreenSaysNothingAboutNearbyPhonesWhenTheyCanHear()
    {
        _ctx.Services.AddSingleton<AetherNetNodeService.Client.IAetherNetServiceSettings>(new FakeServiceSettings());
        _node.Report = new HelpReport { Mine = new HelpState { Guardians = [new HelpGuardian(Sipho, "Sipho")] } };

        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.DoesNotContain("Phones next to you cannot hear you yet", page.Markup);
        Assert.DoesNotContain("Put it right", page.Markup);
    }

    /// <summary>A phone whose settings page can be opened, remembering which page was asked for.</summary>
    private sealed class FakeServiceSettings : AetherNetNodeService.Client.IAetherNetServiceSettings
    {
        public PermissionPage? Opened { get; private set; }

        public string PermissionName => "nearby devices";

        public string WayThere => "AetherNetService on this phone";

        public string Device => "phone";

        public bool Open()
        {
            Opened = PermissionPage.AppInfo;
            return true;
        }

        public bool Open(PermissionPage page)
        {
            Opened = page;
            return true;
        }
    }

    [Fact]
    public async Task AScreenThatCouldNotListenTriesAgainRatherThanGoingStillForEver()
    {
        // Asked for once and nothing retried it: a service not up at that moment left the screen never updating
        // again, on the one feature where somebody is waiting to be told.
        _node.RefuseListening = 1;
        var help = new QuietHelpService(_node);

        help.Listen();
        Assert.False(help.Live);
        Assert.Equal(0, _node.Subscriptions);

        // The next refresh asks again, and from then on the node's own news arrives.
        await help.RefreshAsync();

        Assert.True(help.Live);
        Assert.Equal(1, _node.Subscriptions);

        // And it does not keep subscribing once it is listening.
        await help.RefreshAsync();
        Assert.Equal(1, _node.Subscriptions);
    }

    [Fact]
    public void EveryContainerIsNamedForItselfAndNoneWearsAnothersWords()
    {
        // The two-half form shipped wearing the long advert's description — "needs Bluetooth 5 at both ends" — on a
        // phone that has no Bluetooth 5 and was sending it perfectly well. A default case did that, so now each is
        // named, and anything new is loud about being unnamed rather than quietly wrong.
        _node.Report = new HelpReport
        {
            Mine = new HelpState
            {
                Guardians = [new HelpGuardian(Sipho, "Sipho")],
                Adverts =
                [
                    new HelpAdvertChoice(HelpAdvertForm.Registered16, false, false, "needs a registered Bluetooth ID"),
                    new HelpAdvertChoice(HelpAdvertForm.AetherNet128Pair, true, true),
                    new HelpAdvertChoice(HelpAdvertForm.AetherNet128, false, false, "this phone's Bluetooth is too old"),
                ],
            },
        };

        var page = _ctx.RenderComponent<QuietHelp>();

        Assert.Contains("In two halves", page.Markup);
        Assert.Contains("every phone can hear it, and it costs nothing", page.Markup);

        // And the one actually in use is not described as needing something this phone has not got.
        var inUse = page.FindAll("button.about").First(b => b.TextContent.Contains("In two halves"));
        Assert.Contains("in use", inUse.TextContent);
        Assert.DoesNotContain("needs Bluetooth 5 at both ends", inUse.TextContent);
    }
}

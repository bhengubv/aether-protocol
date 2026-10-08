// SPDX-License-Identifier: MIT

extern alias service;

using AetherNet.Sample.Shared.Pages;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using AetherNetNodeService.Client;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using service::AetherNet.Identity;
using service::AetherNetNodeService;
using service::AetherNetNodeService.Host;
using service::AetherNetNodeService.Host.Data;
using service::AetherNetNodeService.Host.Tests.Fakes;
using Xunit;
using AetherStore = service::AetherNetNodeService.Host.Data.AetherStore;
using PanicWipeService = service::AetherNetNodeService.Host.PanicWipeService;
using PermissionPage = global::AetherNetNodeService.PermissionPage;   // the page's own: its settings link takes it
using ProxyDirectory = service::AetherNet.Mesh.ProxyDirectory;
using RadioStatus = service::AetherNetNodeService.RadioStatus;   // what AetherNetService reports, not the mesh's own
using ServicePermissionPage = service::AetherNetNodeService.PermissionPage;   // where AetherNetService says it is changed

namespace AetherNet.Sample.Tests;

/// <summary>
/// AetherNetService's settings and permissions, on Aether's Settings page.
///
/// <para>
/// AetherNetService has no screen of its own, so Aether's settings are where a person sees what it has been allowed —
/// always, not only while something is missing. Each line opens the service's page in the phone's settings, the only
/// place a permission can change: the phone keeps permissions per app, and one app cannot grant another's.
/// </para>
///
/// <para>
/// And the AetherNet switch is the service's: on a phone with AetherNetService it switches the nearby radios for every
/// app there, and shows what the service says rather than what this app last wrote down.
/// </para>
/// </summary>
[Collection(ServiceInProcessCollection.Name)]
public sealed class SettingsAetherNetServiceTests : IDisposable
{
    private const string Me = "KXJB7-MN2P4";
    private const string Line = "Let AetherNet find phones nearby";   // an older service's one line

    private readonly Bunit.TestContext _ctx = new();
    private readonly AetherStore _store = AetherStore.InMemory();
    private readonly FakeNode _node = new();
    private readonly FakeServiceSettings _settings = new();

    public SettingsAetherNetServiceTests()
    {
        // The theme call is best-effort JS the page wraps in try/catch.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // What Settings.razor @injects — real services where they build cheaply from the test fakes.
        // AetherNetService's ones are made on its side, and the page reaches them through the menu; the theme is the
        // app's own.
        var me = new FakeIdentity(Me);
        var service = new ServiceCollection();
        service.AddSingleton(ConvergedChat.Build(_store, me, new FakeSignalProtocol(), new FakePreKeyExchange(), new FakeRadioMesh(Me)));
        service.AddSingleton(new ProxyDirectory(_store));
        service.AddSingleton(_store);
        service.AddSingleton<IAetherNodeClient>(_node);
        service.AddSingleton(new PanicWipeService(_store, new FakeVault()));
        _ctx.Services.AddServiceInProcess(service);
        _ctx.Services.AddSingleton<IAppTheme>(new NullAppTheme());
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _store.Dispose();
    }

    private static ServicePermission Nearby(bool allowed) => new("Nearby devices", allowed, "find phones near you, over Wi-Fi and Bluetooth");

    private static ServicePermission Notifications(bool allowed) => new("Notifications", allowed, "show that it is keeping you reachable");

    /// <summary>What AetherNetService reports: a radio, and the permissions the phone keeps for it.</summary>
    private static NodeLinkStatus Holding(params ServicePermission[] permissions) =>
        new(false, null, [new RadioStatus("Wi-Fi", true, false, 0)]) { Permissions = permissions };

    private static RadioStatus Waiting(string name) =>
        new(name, false, false, 0) { Reason = "needs permission to find phones nearby", Fixable = true, NeedsPermission = true };

    /// <summary>The line for one permission, found by its name.</summary>
    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Settings> cut, string name) =>
        cut.FindAll("button.about").Single(b => b.QuerySelector(".about-t")?.TextContent == name);

    [Fact]
    public void Every_permission_AetherNetService_holds_shows_whether_it_is_allowed()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(true), Notifications(false));

        var cut = _ctx.RenderComponent<Settings>();

        var nearby = Row(cut, "Nearby devices");
        Assert.Equal("allowed — AetherNetService can find phones near you, over Wi-Fi and Bluetooth", nearby.QuerySelector(".about-s")!.TextContent);
        Assert.Equal("✓", nearby.QuerySelector(".chev")!.TextContent);

        var notifications = Row(cut, "Notifications");
        Assert.Equal("not allowed — allow it so AetherNetService can show that it is keeping you reachable", notifications.QuerySelector(".about-s")!.TextContent);
        Assert.Equal("→", notifications.QuerySelector(".chev")!.TextContent);
    }

    [Fact]
    public void A_permission_opens_AetherNetService_page_in_the_phone_settings()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(false));

        Row(_ctx.RenderComponent<Settings>(), "Nearby devices").Click();

        Assert.Equal(1, _settings.Opened);
    }

    /// <summary>
    /// The gap this closed: the old line went away once the radios were allowed, and took the only way to
    /// AetherNetService's page with it.
    /// </summary>
    [Fact]
    public void The_permissions_stay_on_show_once_everything_is_allowed()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(true), Notifications(true));

        var cut = _ctx.RenderComponent<Settings>();

        Assert.StartsWith("allowed", Row(cut, "Notifications").QuerySelector(".about-s")!.TextContent);
        Row(cut, "Nearby devices").Click();
        Assert.Equal(1, _settings.Opened);
    }

    [Fact]
    public void A_permission_allowed_on_the_phone_shows_as_soon_as_AetherNetService_says_so()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(false));
        var cut = _ctx.RenderComponent<Settings>();
        Assert.StartsWith("not allowed", Row(cut, "Nearby devices").QuerySelector(".about-s")!.TextContent);

        // AetherNetService notices the change by itself — the phone does not tell it — and pushes it.
        _node.Push(Holding(Nearby(true)));

        cut.WaitForAssertion(() => Assert.StartsWith("allowed", Row(cut, "Nearby devices").QuerySelector(".about-s")!.TextContent));
    }

    /// <summary>They are AetherNetService's: switching AetherNet off here changes nothing the phone has allowed it.</summary>
    [Fact]
    public void The_permissions_show_whether_or_not_AetherNet_is_switched_on()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _store.SetFlag(SetupKeys.AetherNet, false);
        _node.Link = Holding(Nearby(false));

        Assert.StartsWith("not allowed", Row(_ctx.RenderComponent<Settings>(), "Nearby devices").QuerySelector(".about-s")!.TextContent);
    }

    /// <summary>A head with no AetherNetService — the web build, the desktop — has no page to send anyone to.</summary>
    [Fact]
    public void Nothing_is_shown_where_there_is_no_AetherNetService_page()
    {
        _node.Link = Holding(Nearby(false));

        var markup = _ctx.RenderComponent<Settings>().Markup;

        Assert.DoesNotContain("Nearby devices", markup);
        Assert.DoesNotContain("AetherNetService's permissions", markup);
        Assert.DoesNotContain(Line, markup);
    }

    [Fact]
    public void A_phone_that_will_not_open_its_settings_says_where_to_look()
    {
        _settings.Opens = false;
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(false));
        var cut = _ctx.RenderComponent<Settings>();

        Row(cut, "Nearby devices").Click();

        Assert.Contains("find AetherNetService under Settings → Apps", cut.Markup);
    }

    // ── What keeps it running ───────────────────────────────────────────────────

    private static ServicePermission Battery(bool allowed) =>
        new("Battery", allowed, "keep running in the background, free of the phone's battery limits") { Page = ServicePermissionPage.Battery };

    private static ServicePermission AppLaunch() =>
        new("App launch", false, "start again after the phone stops it") { Page = ServicePermissionPage.AppLaunch, Known = false };

    /// <summary>
    /// A phone short of memory stops even a foreground service. The battery prompt is the phone's own, raised from here
    /// for AetherNetService; App launch is the phone maker's page, which says nothing back about how it is set.
    /// </summary>
    [Fact]
    public void Battery_asks_the_phone_and_App_launch_opens_the_makers_page()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(true), Battery(false), AppLaunch());
        var cut = _ctx.RenderComponent<Settings>();

        Row(cut, "Battery").Click();
        Row(cut, "App launch").Click();

        Assert.Equal(new[] { PermissionPage.Battery, PermissionPage.AppLaunch }, _settings.Pages);
        var appLaunch = Row(cut, "App launch");
        Assert.Equal("the phone does not say — turn its switches on there so AetherNetService can start again after the phone stops it",
            appLaunch.QuerySelector(".about-s")!.TextContent);
        Assert.Equal("→", appLaunch.QuerySelector(".chev")!.TextContent);
    }

    /// <summary>Where the phone keeps the page to itself, the service says the way there, and the line says it.</summary>
    [Fact]
    public void A_page_the_phone_keeps_to_itself_is_explained_in_the_phones_words()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(AppLaunch() with
        {
            How = "in Battery, open App launch and set AetherNetService to Manage manually, with all three switches on",
        });

        var line = Row(_ctx.RenderComponent<Settings>(), "App launch").QuerySelector(".about-s")!.TextContent;

        Assert.Equal("the phone does not say — in Battery, open App launch and set AetherNetService to Manage manually, "
            + "with all three switches on, so it can start again after the phone stops it", line);
    }

    [Fact]
    public void Something_already_allowed_opens_App_info_where_it_can_be_seen()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Battery(true));

        Row(_ctx.RenderComponent<Settings>(), "Battery").Click();

        Assert.Equal(new[] { PermissionPage.AppInfo }, _settings.Pages);
    }

    // ── An older AetherNetService, which sends no list ──────────────────────────

    [Fact]
    public void An_older_service_with_a_radio_waiting_on_the_permission_still_offers_the_way_to_it()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = new NodeLinkStatus(false, null, [Waiting("Wi-Fi Direct"), new RadioStatus("Wi-Fi", true, false, 0)]);

        var cut = _ctx.RenderComponent<Settings>();

        var line = cut.FindAll("button.about").Single(b => b.TextContent.Contains(Line));
        Assert.Contains("allow Nearby devices for AetherNetService", line.TextContent);

        line.Click();
        Assert.Equal(1, _settings.Opened);
    }

    [Fact]
    public void An_older_service_with_nothing_waiting_still_leaves_the_way_to_its_page()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = new NodeLinkStatus(false, null, [new RadioStatus("Wi-Fi", true, false, 0)]);

        var cut = _ctx.RenderComponent<Settings>();

        Assert.DoesNotContain(Line, cut.Markup);
        cut.FindAll("button.about").Single(b => b.TextContent.Contains("AetherNetService's permissions")).Click();
        Assert.Equal(1, _settings.Opened);
    }

    // ── The AetherNet switch ────────────────────────────────────────────────────

    /// <summary>The switch on the page, found by what it says.</summary>
    private static AngleSharp.Dom.IElement Switch(IRenderedComponent<Settings> cut) =>
        cut.FindAll("button.about").Single(b => b.QuerySelector(".about-t")?.TextContent.StartsWith("AetherNet is") == true);

    [Fact]
    public void On_a_phone_with_AetherNetService_the_switch_shows_what_the_service_says()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _store.SetFlag(SetupKeys.AetherNet, true);   // what this app last wrote down — not the answer
        _node.Link = Holding(Nearby(true)) with { NearbyOn = false };

        var sw = Switch(_ctx.RenderComponent<Settings>());

        Assert.Equal("AetherNet is off", sw.QuerySelector(".about-t")!.TextContent);
        Assert.Contains("internet only, for every app on this phone", sw.TextContent);
    }

    [Fact]
    public void Turning_it_off_switches_the_service_and_shows_off_once_it_comes_back()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(true));
        var cut = _ctx.RenderComponent<Settings>();

        Switch(cut).Click();

        Assert.Equal(new[] { false }, _node.Switched);
        cut.WaitForAssertion(() => Assert.Equal("AetherNet is off", Switch(cut).QuerySelector(".about-t")!.TextContent));
        Assert.Equal("0", _store.GetSetting(SetupKeys.AetherNet));   // this app's own record agrees with the phone's
    }

    [Fact]
    public void A_service_that_cannot_switch_says_so_and_nothing_changes()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Holding(Nearby(true));
        _node.Refuses = true;
        var cut = _ctx.RenderComponent<Settings>();

        Switch(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains("cannot switch its radios — update it", Switch(cut).TextContent));
        Assert.Equal("AetherNet is on", Switch(cut).QuerySelector(".about-t")!.TextContent);
        Assert.NotEqual("0", _store.GetSetting(SetupKeys.AetherNet));
    }

    /// <summary>A head with no AetherNetService runs its own radios: the switch is this app's, applied at launch.</summary>
    [Fact]
    public void Without_AetherNetService_the_switch_is_this_apps_own_and_asks_for_a_reopen()
    {
        _node.Link = Holding(Nearby(true));
        var cut = _ctx.RenderComponent<Settings>();

        Switch(cut).Click();

        Assert.Empty(_node.Switched);
        Assert.Equal("0", _store.GetSetting(SetupKeys.AetherNet));
        Assert.Contains("reopen Aether for this to take effect", Switch(cut).TextContent);
    }

    // ── Each radio, under the one switch ────────────────────────────────────────

    /// <summary>What AetherNetService reports: its radios, each on or off.</summary>
    private static NodeLinkStatus Radios(params RadioStatus[] radios) => new(false, null, radios);

    [Fact]
    public void Every_radio_has_its_own_switch_saying_what_on_and_off_each_mean()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Radios(new RadioStatus("BLE", true, false, 0), new RadioStatus("Wi-Fi Direct", true, false, 0) { On = false });

        var cut = _ctx.RenderComponent<Settings>();

        var bluetooth = Row(cut, "Bluetooth");
        Assert.Equal("on", bluetooth.QuerySelector(".chev")!.TextContent);
        var line = bluetooth.QuerySelector(".about-s")!.TextContent;
        Assert.StartsWith("On. On, it finds devices near you", line);
        Assert.Contains("Off, where there is no Wi-Fi, devices near you cannot reach you", line);

        var wifiDirect = Row(cut, "Wi-Fi Direct");
        Assert.Equal("off", wifiDirect.QuerySelector(".chev")!.TextContent);
        Assert.StartsWith("Switched off.", wifiDirect.QuerySelector(".about-s")!.TextContent);

        // And the one switch for all of them is still there, above them.
        Assert.Equal("AetherNet is on", Switch(cut).QuerySelector(".about-t")!.TextContent);
    }

    [Fact]
    public void Switching_a_radio_switches_it_in_the_service_for_every_app()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Radios(new RadioStatus("BLE", true, false, 0));
        var cut = _ctx.RenderComponent<Settings>();

        Row(cut, "Bluetooth").Click();

        Assert.Equal(new[] { ("BLE", false) }, _node.RadiosSwitched);
        cut.WaitForAssertion(() => Assert.Equal("off", Row(cut, "Bluetooth").QuerySelector(".chev")!.TextContent));
    }

    [Fact]
    public void A_radio_held_down_by_the_AetherNet_switch_says_so()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Radios(new RadioStatus("BLE", true, false, 0), new RadioStatus("Internet", true, false, 0)) with { NearbyOn = false };

        var cut = _ctx.RenderComponent<Settings>();

        Assert.StartsWith("Off while AetherNet is off.", Row(cut, "Bluetooth").QuerySelector(".about-s")!.TextContent);
        Assert.StartsWith("On.", Row(cut, "Internet relay").QuerySelector(".about-s")!.TextContent);   // not a nearby radio
    }

    [Fact]
    public void A_service_that_cannot_switch_one_radio_says_so()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Radios(new RadioStatus("BLE", true, false, 0));
        _node.Refuses = true;
        var cut = _ctx.RenderComponent<Settings>();

        Row(cut, "Bluetooth").Click();

        cut.WaitForAssertion(() => Assert.Contains("cannot switch one radio at a time — update it", cut.Markup));
        Assert.Equal("on", Row(cut, "Bluetooth").QuerySelector(".chev")!.TextContent);
    }

    /// <summary>A head with no AetherNetService has no radios of the service's to switch.</summary>
    [Fact]
    public void Without_AetherNetService_there_are_no_radio_switches()
    {
        _node.Link = Radios(new RadioStatus("BLE", true, false, 0));

        Assert.DoesNotContain("Bluetooth", _ctx.RenderComponent<Settings>().Markup);
    }

    /// <summary>On a computer the page says computer, and the way to the service is where it keeps its things.</summary>
    [Fact]
    public void On_a_computer_it_says_computer_and_where_the_service_keeps_its_things()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(new ComputerSettings());
        _node.Link = Radios(new RadioStatus("Wi-Fi", true, false, 0)) with { NearbyOn = false };

        var cut = _ctx.RenderComponent<Settings>();

        Assert.Contains("internet only, for every app on this computer", Switch(cut).TextContent);
        var way = Row(cut, "Where AetherNetService keeps its things");
        Assert.Equal("its identity and its log, in its folder", way.QuerySelector(".about-s")!.TextContent);
    }

    /// <summary>A computer's AetherNetService settings: no permissions, and a folder.</summary>
    private sealed class ComputerSettings : IAetherNetServiceSettings
    {
        public string PermissionName => "nothing";
        public bool Open() => true;
        public string Device => "computer";
        public string WayThere => "Where AetherNetService keeps its things";
        public string Where => "its identity and its log, in its folder";
    }

    /// <summary>AetherNetService's settings page, counting how often it was opened.</summary>
    private sealed class FakeServiceSettings : IAetherNetServiceSettings
    {
        public int Opened { get; private set; }

        public bool Opens { get; set; } = true;

        public string PermissionName => "Nearby devices";

        /// <summary>Which pages were asked for, in order.</summary>
        public List<PermissionPage> Pages { get; } = [];

        public bool Open()
        {
            Opened++;
            return Opens;
        }

        public bool Open(PermissionPage page)
        {
            Pages.Add(page);
            return Open();
        }
    }

    /// <summary>The node as the page sees it: a link state, and a way to push a new one.</summary>
    private sealed class FakeNode : IAetherNodeClient
    {
        private readonly List<IAetherNodeEvents> _listeners = [];

        public NodeLinkStatus Link { get; set; } = NodeLinkStatus.Offline;

        public void Push(NodeLinkStatus status)
        {
            Link = status;
            foreach (var listener in _listeners.ToArray()) listener.OnLinkChanged(status);
        }

        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default) => Task.FromResult(Link);

        /// <summary>What the switch was asked, in order.</summary>
        public List<bool> Switched { get; } = [];

        /// <summary>An AetherNetService with no switch to turn.</summary>
        public bool Refuses { get; set; }

        /// <summary>What the radio switches were asked, in order.</summary>
        public List<(string Radio, bool On)> RadiosSwitched { get; } = [];

        // As with the AetherNet switch: the real service restarts and comes back with it; this one has it at once.
        public Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default)
        {
            if (Refuses) throw new AetherNodeException(AetherNodeErrorCode.Internal, "this service cannot switch one radio at a time");
            RadiosSwitched.Add((radio, on));
            Link = Link with { Radios = Link.Radios.Select(r => r.Name == radio ? r with { On = on } : r).ToArray() };
            return Task.CompletedTask;
        }

        // The real service restarts to apply it and comes back with the new state; this one has it at once.
        public Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
        {
            if (Refuses) throw new AetherNodeException(AetherNodeErrorCode.Internal, "this service has no nearby radios to switch");
            Switched.Add(on);
            Link = Link with { NearbyOn = on };
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(IAetherNodeEvents listener)
        {
            _listeners.Add(listener);
            return new Unsubscribe(() => _listeners.Remove(listener));
        }

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Unsubscribe(Action undo) : IDisposable
        {
            public void Dispose() => undo();
        }
    }

    private sealed class FakeVault : ISecretVault
    {
        private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

        public bool IsHardwareBacked => true;
        public string ProtectionDescription => "Test vault";
        public bool Has(string name) => _secrets.ContainsKey(name);
        public void Set(string name, byte[] secret) => _secrets[name] = secret;
        public byte[]? Get(string name) => _secrets.TryGetValue(name, out var s) ? s : null;
        public void Remove(string name) => _secrets.Remove(name);
    }
}

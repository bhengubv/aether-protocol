// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Pages;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using AetherNetNodeService;
using AetherNetNodeService.Client;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RadioStatus = AetherNetNodeService.RadioStatus;   // what AetherNetService reports, not the mesh's own

namespace AetherNet.Sample.Tests;

/// <summary>
/// AetherNetService's permissions, on Aether's Settings page.
///
/// <para>
/// AetherNetService has no screen of its own, so Aether's settings are where a person sees what it has been allowed —
/// always, not only while something is missing. Each line opens the service's page in the phone's settings, the only
/// place a permission can change: the phone keeps permissions per app, and one app cannot grant another's.
/// </para>
/// </summary>
public sealed class SettingsPermissionTests : IDisposable
{
    private const string Me = "KXJB7-MN2P4";
    private const string Line = "Let AetherNet find phones nearby";   // an older service's one line

    private readonly Bunit.TestContext _ctx = new();
    private readonly AetherStore _store = AetherStore.InMemory();
    private readonly FakeNode _node = new();
    private readonly FakeServiceSettings _settings = new();

    public SettingsPermissionTests()
    {
        // The theme call is best-effort JS the page wraps in try/catch.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // What Settings.razor @injects — real services where they build cheaply from the test fakes.
        var me = new FakeIdentity(Me);
        _ctx.Services.AddSingleton(ConvergedChat.Build(_store, me, new FakeSignalProtocol(), new FakePreKeyExchange(), new FakeRadioMesh(Me)));
        _ctx.Services.AddSingleton(new ProxyDirectory(_store));
        _ctx.Services.AddSingleton(_store);
        _ctx.Services.AddSingleton<IAppTheme>(new NullAppTheme());
        _ctx.Services.AddSingleton<IAetherNodeClient>(_node);
        _ctx.Services.AddSingleton(new PanicWipeService(_store, new FakeVault()));
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

    /// <summary>AetherNetService's settings page, counting how often it was opened.</summary>
    private sealed class FakeServiceSettings : IAetherNetServiceSettings
    {
        public int Opened { get; private set; }

        public bool Opens { get; set; } = true;

        public string PermissionName => "Nearby devices";

        public bool Open()
        {
            Opened++;
            return Opens;
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

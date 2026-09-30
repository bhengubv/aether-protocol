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
/// The way to AetherNetService's permission, on Aether's Settings page.
///
/// <para>
/// AetherNetService has no screen to ask from, and the phone keeps permissions per app — so switching AetherNet on
/// here cannot grant the radios anything. While a radio is actually waiting on that permission, the page offers the
/// service's own page in the phone's settings, and nothing otherwise.
/// </para>
/// </summary>
public sealed class SettingsPermissionTests : IDisposable
{
    private const string Me = "KXJB7-MN2P4";
    private const string Line = "Let AetherNet find phones nearby";

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

    private static RadioStatus Waiting(string name) =>
        new(name, false, false, 0) { Reason = "needs permission to find phones nearby", Fixable = true, NeedsPermission = true };

    [Fact]
    public void A_radio_waiting_on_the_permission_offers_the_way_to_it()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = new NodeLinkStatus(false, null, [Waiting("Wi-Fi Direct"), new RadioStatus("Wi-Fi", true, false, 0)]);

        var cut = _ctx.RenderComponent<Settings>();

        var line = cut.FindAll("button.about").Single(b => b.TextContent.Contains(Line));
        Assert.Contains("allow Nearby devices for AetherNetService", line.TextContent);

        line.Click();
        Assert.Equal(1, _settings.Opened);
    }

    /// <summary>A switched-off radio is fixable too — but not on the page that grants permissions.</summary>
    [Fact]
    public void Nothing_is_offered_when_no_radio_is_waiting_on_it()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = new NodeLinkStatus(false, null,
        [
            new RadioStatus("BLE", false, false, 0) { Reason = "Bluetooth is switched off", Fixable = true },
            new RadioStatus("Wi-Fi", true, false, 0),
        ]);

        Assert.DoesNotContain(Line, _ctx.RenderComponent<Settings>().Markup);
    }

    [Fact]
    public void Nothing_is_offered_while_AetherNet_is_switched_off()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _store.SetFlag(SetupKeys.AetherNet, false);
        _node.Link = new NodeLinkStatus(false, null, [Waiting("Wi-Fi Direct")]);

        Assert.DoesNotContain(Line, _ctx.RenderComponent<Settings>().Markup);
    }

    /// <summary>A head with no AetherNetService — the web build, the desktop — has no page to send anyone to.</summary>
    [Fact]
    public void Nothing_is_offered_where_there_is_no_AetherNetService_page()
    {
        _node.Link = new NodeLinkStatus(false, null, [Waiting("Wi-Fi Direct")]);

        Assert.DoesNotContain(Line, _ctx.RenderComponent<Settings>().Markup);
    }

    [Fact]
    public void The_line_goes_once_the_radios_have_the_permission()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = new NodeLinkStatus(false, null, [Waiting("Wi-Fi Direct")]);
        var cut = _ctx.RenderComponent<Settings>();
        Assert.Contains(Line, cut.Markup);

        // AetherNetService pushes the new state once the radio is up.
        _node.Push(new NodeLinkStatus(false, null, [new RadioStatus("Wi-Fi Direct", true, false, 0)]));

        cut.WaitForAssertion(() => Assert.DoesNotContain(Line, cut.Markup));
    }

    [Fact]
    public void A_phone_that_will_not_open_its_settings_says_where_to_look()
    {
        _settings.Opens = false;
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = new NodeLinkStatus(false, null, [Waiting("Wi-Fi Direct")]);
        var cut = _ctx.RenderComponent<Settings>();

        cut.FindAll("button.about").Single(b => b.TextContent.Contains(Line)).Click();

        Assert.Contains("find AetherNetService under Settings → Apps", cut.Markup);
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

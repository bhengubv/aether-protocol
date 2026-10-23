// SPDX-License-Identifier: MIT

extern alias service;

using Aether.Shared.Pages;
using Aether.Tests.Fakes;
using AetherNetNodeService.Client;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using service::AetherNet.Identity;
using service::AetherNetNodeService;
using service::AetherNetNodeService.Host.Tests.Fakes;
using Xunit;
using AetherStore = service::AetherNetNodeService.Host.Data.AetherStore;
using PermissionPage = global::AetherNetNodeService.PermissionPage;   // the page's own: Allow takes it there
using RadioStatus = service::AetherNetNodeService.RadioStatus;   // what AetherNetService reports, not the mesh's own

namespace Aether.Tests;

/// <summary>
/// The welcome's radios, on a phone where they are AetherNetService's.
///
/// <para>
/// A radio held back by a permission offers Allow. AetherNetService has no screen to ask from, and the phone keeps a
/// permission per app, so Allow is the way to the service's own page in the phone's settings — opened by the app, where
/// the person is. Asked of AetherNetService instead, the request found no page to open there, and Allow did nothing at
/// all (Aether 1.4 and 1.5; seen on a P30, 2026-10-23).
/// </para>
/// </summary>
[Collection(ServiceInProcessCollection.Name)]
public sealed class WelcomeRadiosTests : IDisposable
{
    private const string Me = "KXJB7-MN2P4";

    private readonly Bunit.TestContext _ctx = new();
    private readonly AetherStore _store = AetherStore.InMemory();
    private readonly FakeNode _node = new();
    private readonly FakeServiceSettings _settings = new();

    public WelcomeRadiosTests()
    {
        // What Welcome.razor @injects, made on AetherNetService's side and reached through the menu. The radios are the
        // service's own NodeRadioSetup, made as AetherNetService makes it: with no settings page of its own to open.
        var service = new ServiceCollection();
        service.AddSingleton(_store);
        service.AddSingleton<service::AetherNet.Mesh.IIdentityService>(new FakeIdentity(Me));
        service.AddSingleton<IAetherNodeClient>(_node);
        service.AddSingleton<service::AetherNet.Mesh.IRadioSetup>(new service::AetherNetNodeService.Client.NodeRadioSetup(_node));
        _ctx.Services.AddServiceInProcess(service);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _store.Dispose();
    }

    /// <summary>The two radios a P30 had waiting on its first run, as AetherNetService reports them.</summary>
    private static NodeLinkStatus Waiting() => new(false, null, [Held("Wi-Fi Direct"), Held("BLE")]);

    private static RadioStatus Held(string name) =>
        new(name, false, false, 0) { Reason = "needs permission to find phones nearby", Fixable = true, NeedsPermission = true };

    /// <summary>The same radios once the permission is allowed.</summary>
    private static NodeLinkStatus Allowed() =>
        new(true, "BLE", [new RadioStatus("Wi-Fi Direct", true, false, 0), new RadioStatus("BLE", true, true, 1_000_000)]);

    /// <summary>The welcome, walked to its radios: past the hello, the AetherTag and the AetherNet switch.</summary>
    private IRenderedComponent<Welcome> AtTheRadios()
    {
        var cut = _ctx.RenderComponent<Welcome>();
        foreach (var next in new[] { "Your name on Aether", "Switch on AetherNet?", "Switch on your radios" })
        {
            cut.Find(".wiz-foot button.primary").Click();
            cut.WaitForAssertion(() => Assert.Equal(next, cut.Find(".pane h1").TextContent));
        }
        return cut;
    }

    /// <summary>One radio's card, found by its name.</summary>
    private static AngleSharp.Dom.IElement Card(IRenderedComponent<Welcome> cut, string name) =>
        cut.FindAll(".radio").Single(c => c.QuerySelector(".r-name")?.TextContent == name);

    [Fact]
    public void Allow_opens_AetherNetService_page_in_the_phone_settings()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Waiting();
        var cut = AtTheRadios();

        var allow = Card(cut, "Wi-Fi Direct").QuerySelector("button.r-btn")!;
        Assert.Equal("Allow", allow.TextContent);
        allow.Click();

        Assert.Equal([PermissionPage.AppInfo], _settings.Pages);
    }

    /// <summary>
    /// The phone tells no app when a permission is allowed on its own page; AetherNetService looks again every few
    /// seconds and says so. Back on the welcome, the radios show it with nothing pressed.
    /// </summary>
    [Fact]
    public void Radios_allowed_on_the_phone_show_as_soon_as_AetherNetService_says_so()
    {
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Waiting();
        var cut = AtTheRadios();
        Card(cut, "BLE").QuerySelector("button.r-btn")!.Click();

        _node.Push(Allowed());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("ready", Card(cut, "Wi-Fi Direct").QuerySelector(".r-pill")!.TextContent);
            Assert.Equal("ready", Card(cut, "BLE").QuerySelector(".r-pill")!.TextContent);
            Assert.Empty(cut.FindAll("button.r-btn"));
        });
    }

    /// <summary>A phone that will not open the page: the person is told where it is, not left with nothing.</summary>
    [Fact]
    public void When_the_phone_will_not_open_the_page_the_person_is_told_where_it_is()
    {
        _settings.Opens = false;
        _ctx.Services.AddSingleton<IAetherNetServiceSettings>(_settings);
        _node.Link = Waiting();
        var cut = AtTheRadios();

        Card(cut, "Wi-Fi Direct").QuerySelector("button.r-btn")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("find AetherNetService under Settings, then Apps", cut.Markup));
    }

    /// <summary>AetherNetService's settings page, counting how often it was opened.</summary>
    private sealed class FakeServiceSettings : IAetherNetServiceSettings
    {
        public int Opened { get; private set; }

        public bool Opens { get; set; } = true;

        public string PermissionName => "Location";

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

    /// <summary>The node as AetherNetService holds it: a link state, and a way to push a new one.</summary>
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

        public Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
        {
            Link = Link with { NearbyOn = on };
            return Task.CompletedTask;
        }

        public Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
}

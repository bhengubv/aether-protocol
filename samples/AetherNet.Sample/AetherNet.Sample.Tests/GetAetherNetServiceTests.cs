// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Pages;
using AetherNetNodeService;
using AetherNetNodeService.Client;
using Bunit;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The screen a phone with Aether alone sees first: AetherNetService, asked for — and fetched only on the person's yes,
/// only if it checks out, and installed only through the phone's own installer.
/// </summary>
public sealed class GetAetherNetServiceTests : IDisposable
{
    private readonly Bunit.TestContext _ctx = new();
    private readonly Phone _phone = new();
    private readonly Store _store = new();
    private readonly Verifier _verifier = new();
    private readonly Installer _installer = new();

    public void Dispose() => _ctx.Dispose();

    private NodeInstallFlow Flow() => new(_phone, _store, _verifier, _installer);

    private IRenderedComponent<GetAetherNetService> Show(NodeInstallFlow flow) =>
        _ctx.RenderComponent<GetAetherNetService>(p => p.Add(c => c.Flow, flow));

    [Fact]
    public async Task It_asks_for_AetherNetService_and_says_where_from_and_how_big()
    {
        var flow = Flow();
        await flow.CheckAsync();

        var cut = Show(flow);

        Assert.Equal("Aether needs AetherNetService", cut.Find("h1").TextContent);
        // In the person's own way of writing numbers: "33,4 MB" on a South African phone, "33.4 MB" elsewhere.
        var size = $"{35_000_000 / 1048576.0:0.0} MB";
        Assert.Contains($"Get it from SleptOn — {size}.", cut.FindAll("p.lede").Select(p => p.TextContent));
        Assert.Equal("Download and install", cut.Find("button").TextContent);
        Assert.Equal("Your phone will ask you to confirm the install — the first time, it also asks you to allow installs from Aether.",
            cut.Find(".fineprint").TextContent);
    }

    [Fact]
    public async Task Yes_hands_the_checked_package_to_the_phone_and_says_what_to_do_next()
    {
        var flow = Flow();
        await flow.CheckAsync();
        var cut = Show(flow);

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Finish the install on the screen your phone opened", cut.Markup));
        Assert.Same(_store.Package, _installer.Given);
        Assert.Equal("I've installed it", cut.Find("button").TextContent);
    }

    [Fact]
    public async Task A_download_that_is_not_the_real_one_is_refused_and_said_so()
    {
        _verifier.Says = NodePackageVerdict.No("it is signed by someone else");
        var flow = Flow();
        await flow.CheckAsync();
        var cut = Show(flow);

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("it is signed by someone else", cut.Find(".warn").TextContent));
        Assert.Null(_installer.Given);
        Assert.Equal("Try again", cut.Find("button").TextContent);
    }

    [Fact]
    public async Task A_store_without_it_says_so_and_can_be_asked_again()
    {
        _store.Has = null;
        var flow = Flow();
        await flow.CheckAsync();
        var cut = Show(flow);

        Assert.Equal("SleptOn does not have AetherNetService yet", cut.Find(".warn").TextContent);

        _store.Has = Store.Release;
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Equal("Download and install", cut.Find("button").TextContent));
    }

    /// <summary>While it is still looking, nothing is said — the look takes a moment, and words would flash.</summary>
    [Fact]
    public void While_looking_it_says_nothing()
    {
        var cut = Show(Flow());

        Assert.Empty(cut.FindAll("h1"));
        Assert.Empty(cut.FindAll("button"));
    }

    private sealed class Phone : INodeConnector
    {
        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAetherNodeClient?>(null);
    }

    private sealed class Store : INodePackageStore
    {
        public static readonly NodePackageOffer Release =
            new("1.2", 3, 35_000_000, new Uri("https://api.slepton.test/api/appstore/download/3f2b6c1e-8d4a-4c7e-9b1f-2a6d5e4c3b21"));

        public NodePackageOffer? Has = Release;
        public byte[] Package = [1, 2, 3];

        public string Name => "SleptOn";

        public Task<NodePackageOffer?> FindAsync(CancellationToken cancellationToken = default) => Task.FromResult(Has);

        public Task<byte[]> DownloadAsync(NodePackageOffer offer, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Package);
    }

    private sealed class Verifier : INodePackageVerifier
    {
        public NodePackageVerdict Says = NodePackageVerdict.Yes;
        public NodePackageVerdict Verify(byte[] package) => Says;
    }

    private sealed class Installer : INodePackageInstaller
    {
        public byte[]? Given;

        public Task<bool> RequestInstallAsync(byte[] packageBytes, CancellationToken cancellationToken = default)
        {
            Given = packageBytes;
            return Task.FromResult(true);
        }
    }
}

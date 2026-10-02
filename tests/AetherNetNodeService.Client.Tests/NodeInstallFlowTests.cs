// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNetNodeService.Client.Tests;

/// <summary>
/// Getting AetherNetService onto a phone that has Aether alone: look, offer, and on the person's yes download, check it
/// is the real one, and hand it to the phone's installer. Nothing the check refused is ever installed.
/// </summary>
public class NodeInstallFlowTests
{
    private static readonly NodePackageOffer Offer =
        new("1.2", 3, 4, new Uri("https://api.slepton.test/api/appstore/download/3f2b6c1e-8d4a-4c7e-9b1f-2a6d5e4c3b21"));

    private sealed class FakeConnector : INodeConnector
    {
        public bool Installed;
        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installed);
        public Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAetherNodeClient?>(null);
    }

    private sealed class FakeStore : INodePackageStore
    {
        public NodePackageOffer? Has = Offer;
        public Exception? FindFails;
        public Exception? DownloadFails;
        public byte[] Package = [1, 2, 3, 4];

        public string Name => "SleptOn";

        public Task<NodePackageOffer?> FindAsync(CancellationToken cancellationToken = default) =>
            FindFails is { } ex ? Task.FromException<NodePackageOffer?>(ex) : Task.FromResult(Has);

        public Task<byte[]> DownloadAsync(NodePackageOffer offer, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            if (DownloadFails is { } ex) return Task.FromException<byte[]>(ex);
            progress?.Report(Package.Length);
            return Task.FromResult(Package);
        }
    }

    private sealed class FakeVerifier : INodePackageVerifier
    {
        public NodePackageVerdict Says = NodePackageVerdict.Yes;
        public NodePackageVerdict Verify(byte[] package) => Says;
    }

    private sealed class FakeInstaller : INodePackageInstaller
    {
        public bool Opens = true;
        public byte[]? Given;

        public Task<bool> RequestInstallAsync(byte[] packageBytes, CancellationToken cancellationToken = default)
        {
            Given = packageBytes;
            return Task.FromResult(Opens);
        }
    }

    private readonly FakeConnector _phone = new();
    private readonly FakeStore _store = new();
    private readonly FakeVerifier _verifier = new();
    private readonly FakeInstaller _installer = new();
    private readonly List<string> _log = [];

    private NodeInstallFlow Flow() => new(_phone, _store, _verifier, _installer, _log.Add);

    [Fact]
    public async Task A_phone_that_has_it_is_asked_nothing()
    {
        _phone.Installed = true;
        var flow = Flow();

        await flow.CheckAsync();

        Assert.Equal(NodeInstallStep.Installed, flow.Step);
        Assert.Null(flow.Offer);
    }

    [Fact]
    public async Task A_phone_without_it_is_offered_what_the_store_has()
    {
        var flow = Flow();

        await flow.CheckAsync();

        Assert.Equal(NodeInstallStep.Offered, flow.Step);
        Assert.Same(Offer, flow.Offer);
        Assert.Null(_installer.Given);   // nothing happens before the person says yes
    }

    [Fact]
    public async Task A_store_without_it_says_so()
    {
        _store.Has = null;
        var flow = Flow();

        await flow.CheckAsync();

        Assert.Equal(NodeInstallStep.NotInStore, flow.Step);
        Assert.Equal("SleptOn does not have AetherNetService yet", flow.Problem);
    }

    [Fact]
    public async Task A_store_that_cannot_be_reached_says_why()
    {
        _store.FindFails = new NodePackageException("SleptOn could not be reached");
        var flow = Flow();

        await flow.CheckAsync();

        Assert.Equal(NodeInstallStep.NotInStore, flow.Step);
        Assert.Equal("SleptOn could not be reached", flow.Problem);
    }

    [Fact]
    public async Task Yes_downloads_it_checks_it_and_hands_it_to_the_phone()
    {
        var flow = Flow();
        await flow.CheckAsync();

        await flow.InstallAsync();

        Assert.Equal(NodeInstallStep.Installing, flow.Step);
        Assert.Same(_store.Package, _installer.Given);
        Assert.Equal(_store.Package.Length, flow.Downloaded);
    }

    /// <summary>The one thing that matters most: a download that is not the real one never reaches the installer.</summary>
    [Fact]
    public async Task A_download_that_is_not_the_real_one_is_never_installed()
    {
        _verifier.Says = NodePackageVerdict.No("it is signed by someone else");
        var flow = Flow();
        await flow.CheckAsync();

        await flow.InstallAsync();

        Assert.Equal(NodeInstallStep.Failed, flow.Step);
        Assert.Null(_installer.Given);
        Assert.Equal("not installed — this download is not AetherNetService from Aether's makers: it is signed by someone else", flow.Problem);
        Assert.Contains(_log, line => line.Contains("signed by someone else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_download_says_why_and_can_be_tried_again()
    {
        _store.DownloadFails = new NodePackageException("the download from SleptOn broke off");
        var flow = Flow();
        await flow.CheckAsync();

        await flow.InstallAsync();
        Assert.Equal(NodeInstallStep.Failed, flow.Step);
        Assert.Equal("the download from SleptOn broke off", flow.Problem);

        _store.DownloadFails = null;
        await flow.InstallAsync();
        Assert.Equal(NodeInstallStep.Installing, flow.Step);
        Assert.Null(flow.Problem);
    }

    [Fact]
    public async Task An_installer_that_will_not_open_says_so()
    {
        _installer.Opens = false;
        var flow = Flow();
        await flow.CheckAsync();

        await flow.InstallAsync();

        Assert.Equal(NodeInstallStep.Failed, flow.Step);
        Assert.Equal("the phone's installer did not open", flow.Problem);
    }

    [Fact]
    public async Task Once_installed_the_flow_is_done()
    {
        var flow = Flow();
        await flow.CheckAsync();
        await flow.InstallAsync();

        Assert.False(await flow.RecheckAsync());   // the person has not finished yet
        _phone.Installed = true;
        Assert.True(await flow.RecheckAsync());

        Assert.Equal(NodeInstallStep.Installed, flow.Step);
    }

    [Fact]
    public async Task Nothing_is_downloaded_without_an_offer()
    {
        _phone.Installed = true;
        var flow = Flow();
        await flow.CheckAsync();

        await flow.InstallAsync();

        Assert.Null(_installer.Given);
        Assert.Equal(NodeInstallStep.Installed, flow.Step);
    }
}

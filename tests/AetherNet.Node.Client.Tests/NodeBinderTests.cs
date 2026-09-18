// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNet.Node.Client.Tests;

public class NodeBinderTests
{
    private sealed class FakeConnector : INodeConnector
    {
        public bool Installed;
        public IAetherNodeClient? BindResult;
        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installed);
        public Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default) => Task.FromResult(BindResult);
    }

    private sealed class FakeInstaller : INodePackageInstaller
    {
        public bool Accept = true;
        public byte[]? Installed;
        public Task<bool> RequestInstallAsync(byte[] packageBytes, CancellationToken cancellationToken = default)
        {
            Installed = packageBytes;
            return Task.FromResult(Accept);
        }
    }

    private sealed class FixedSource : INodePackageSource
    {
        private readonly byte[] _bytes;
        public FixedSource(byte[] bytes) => _bytes = bytes;
        public string Name => "fixed";
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<byte[]> FetchAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
            => NodePackageFingerprint.Verify(_bytes, expectedFingerprint)
                ? Task.FromResult(_bytes)
                : throw new NodePackageException("fingerprint mismatch");
    }

    private sealed class StubClient : IAetherNodeClient
    {
        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(AetherNetTag));
        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(System.Array.Empty<byte>());
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.FromResult(System.Array.Empty<byte>());
        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Task.FromResult(OutboundResult.Queued);
        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InboundMessage>>(System.Array.Empty<InboundMessage>());
        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default) => Task.FromResult(NodeLinkStatus.Offline);
        public IDisposable Subscribe(IAetherNodeEvents listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Connect_reports_absent_when_nothing_is_installed()
    {
        var binder = new NodeBinder(new FakeConnector { Installed = false }, new FakeInstaller());
        Assert.Equal(NodeBindState.Absent, await binder.ConnectAsync());
        Assert.Null(binder.Client);
    }

    [Fact]
    public async Task Connect_binds_when_installed_and_granted()
    {
        var client = new StubClient();
        var binder = new NodeBinder(new FakeConnector { Installed = true, BindResult = client }, new FakeInstaller());
        Assert.Equal(NodeBindState.Bound, await binder.ConnectAsync());
        Assert.Same(client, binder.Client);
    }

    [Fact]
    public async Task Connect_awaits_grant_when_installed_but_not_yet_granted()
    {
        var binder = new NodeBinder(new FakeConnector { Installed = true, BindResult = null }, new FakeInstaller());
        Assert.Equal(NodeBindState.AwaitingGrant, await binder.ConnectAsync());
    }

    [Fact]
    public async Task Install_fetches_verified_bytes_and_awaits_grant_on_acceptance()
    {
        var apk = new byte[] { 1, 1, 2, 3, 5, 8 };
        var installer = new FakeInstaller { Accept = true };
        var binder = new NodeBinder(new FakeConnector { Installed = false }, installer);

        var state = await binder.InstallAsync(new FixedSource(apk), NodePackageFingerprint.Compute(apk));

        Assert.Equal(NodeBindState.AwaitingGrant, state);
        Assert.Equal(apk, installer.Installed);
    }

    [Fact]
    public async Task Install_reports_declined_when_the_user_says_no()
    {
        var apk = new byte[] { 7, 7, 7 };
        var binder = new NodeBinder(new FakeConnector(), new FakeInstaller { Accept = false });

        var state = await binder.InstallAsync(new FixedSource(apk), NodePackageFingerprint.Compute(apk));

        Assert.Equal(NodeBindState.InstallDeclined, state);
    }
}

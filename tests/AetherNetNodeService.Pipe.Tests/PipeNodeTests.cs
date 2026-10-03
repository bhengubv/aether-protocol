// SPDX-License-Identifier: MIT

using AetherNetNodeService.Client;
using AetherNetNodeService.Host;
using Xunit;

namespace AetherNetNodeService.Pipe.Tests;

/// <summary>
/// The named pipe between an app and AetherNetService, over real pipes: every call crosses and comes back as itself,
/// errors keep their codes, pushes arrive, and when AetherNetService goes the app hears it and connects again.
/// </summary>
public class PipeNodeTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(10);

    private static string NewPipe() => $"aether-test-{Guid.NewGuid():N}";

    /// <summary>A server on its own pipe, and an app connected to it.</summary>
    private static async Task<(PipeNodeServer Server, PipeNodeClient Client)> ConnectAsync(FakeNode node, IGrantStore? grants = null)
    {
        var pipe = NewPipe();
        var server = new PipeNodeServer(() => node, grants ?? new AdmitAll(), pipe);
        server.Start();
        var client = await new PipeNodeConnector(launcher: null, pipe).TryBindAsync();
        Assert.NotNull(client);
        return (server, Assert.IsType<PipeNodeClient>(client));
    }

    [Fact]
    public async Task Every_call_crosses_and_comes_back()
    {
        var node = new FakeNode();
        var (server, client) = await ConnectAsync(node);
        await using var _ = server;
        using var __ = client;

        Assert.Equal(node.Me, await client.GetTagAsync());
        Assert.Equal(node.PublicKey, await client.GetPublicKeyAsync());
        Assert.Equal(new byte[] { 3, 2, 1 }, await client.SignAsync(new byte[] { 1, 2, 3 }));
        Assert.Equal(new byte[] { 1, 2, 3 }, node.Signed);

        var to = FakeNode.Tag(9);
        var id = Guid.NewGuid();
        var sent = await client.SendAsync(to, new byte[] { 5, 6 }, id);
        Assert.True(sent.Accepted);
        Assert.Equal("queued", sent.Detail);
        Assert.Equal(to, node.Sent!.Value.To);
        Assert.Equal(new byte[] { 5, 6 }, node.Sent.Value.Payload);
        Assert.Equal(id, node.Sent.Value.Id);   // the app's own id, so delivery is reported under it

        await client.MeetAsync([new NodeContact(to, [7, 7], Mutual: true)]);
        var met = Assert.Single(node.Met!);
        Assert.Equal(to, met.Tag);
        Assert.True(met.Mutual);

        node.Inbox.Add(new InboundMessage(to, new byte[] { 8 }, "chat", DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        var inbox = await client.GetInboxAsync(limit: 7);
        Assert.Equal(7, node.InboxLimit);
        Assert.Equal(to, Assert.Single(inbox).From);

        var link = await client.GetLinkAsync();
        Assert.True(link.Linked);
        Assert.Equal("BLE", link.Radio);

        Assert.Equal("one two three", await client.GetRecoveryPhraseAsync());

        await client.SetNearbyAsync(false);
        Assert.False(node.Nearby);

        await client.SetRadioAsync("Wi-Fi Direct", false);
        Assert.Equal(("Wi-Fi Direct", false), node.RadioSwitched);
    }

    [Fact]
    public async Task An_error_keeps_its_code()
    {
        var node = new FakeNode { TagFails = new AetherNodeException(AetherNodeErrorCode.IdentityAbsent, "no identity yet") };
        var (server, client) = await ConnectAsync(node);
        await using var _ = server;
        using var __ = client;

        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => client.GetTagAsync());
        Assert.Equal(AetherNodeErrorCode.IdentityAbsent, ex.Code);
        Assert.Equal("no identity yet", ex.Message);

        // And the connection is still good afterwards.
        Assert.Equal(node.PublicKey, await client.GetPublicKeyAsync());
    }

    [Fact]
    public async Task Any_other_failure_is_internal_not_a_broken_pipe()
    {
        var node = new FakeNode { TagFails = new InvalidOperationException("boom") };
        var (server, client) = await ConnectAsync(node);
        await using var _ = server;
        using var __ = client;

        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => client.GetTagAsync());
        Assert.Equal(AetherNodeErrorCode.Internal, ex.Code);
        Assert.True(client.IsAlive);
    }

    [Fact]
    public async Task Pushes_reach_the_app_in_order()
    {
        var node = new FakeNode();
        var (server, client) = await ConnectAsync(node);
        await using var _ = server;
        using var __ = client;

        var ears = new Ears();
        using var subscription = client.Subscribe(ears);
        await WaitUntilAsync(() => node.Listening == 1);

        var id = Guid.NewGuid();
        node.RaiseLink(NodeLinkStatus.Offline);
        node.RaiseInbound(new InboundMessage(node.Me, new byte[] { 1 }, "chat", DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        node.RaiseDelivered(id);

        await ears.UntilAsync(3, Within);
        Assert.IsType<NodeLinkStatus>(ears.Heard[0]);
        Assert.IsType<InboundMessage>(ears.Heard[1]);
        Assert.Equal(id, ears.Heard[2]);

        // The last listener gone, the service stops pushing to this app.
        subscription.Dispose();
        await WaitUntilAsync(() => node.Listening == 0);
    }

    [Fact]
    public async Task When_the_service_goes_waiting_calls_fail_and_the_app_hears_once()
    {
        var node = new FakeNode { LinkWaits = new TaskCompletionSource<NodeLinkStatus>() };
        var (server, client) = await ConnectAsync(node);
        using var __ = client;

        var died = 0;
        client.Died += () => Interlocked.Increment(ref died);

        var waiting = client.GetLinkAsync();
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        await server.DisposeAsync();   // AetherNetService goes

        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => waiting.WaitAsync(Within));
        Assert.Equal(AetherNodeErrorCode.NodeUnavailable, ex.Code);
        await WaitUntilAsync(() => Volatile.Read(ref died) == 1);
        Assert.False(client.IsAlive);

        // A call after that fails at once, as the same code.
        var after = await Assert.ThrowsAsync<AetherNodeException>(() => client.GetTagAsync());
        Assert.Equal(AetherNodeErrorCode.NodeUnavailable, after.Code);
        Assert.Equal(1, Volatile.Read(ref died));
    }

    [Fact]
    public async Task Letting_go_is_not_the_service_dying()
    {
        var node = new FakeNode();
        var (server, client) = await ConnectAsync(node);
        await using var _ = server;

        var died = false;
        client.Died += () => died = true;
        client.Dispose();
        await Task.Delay(200);

        Assert.False(died);
        Assert.False(client.IsAlive);
    }

    [Fact]
    public async Task An_app_without_a_grant_is_refused_and_its_ask_is_recorded()
    {
        var grants = new InMemoryGrantStore();
        var (server, client) = await ConnectAsync(new FakeNode(), grants);
        await using var _ = server;
        using var __ = client;

        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => client.GetTagAsync());
        Assert.Equal(AetherNodeErrorCode.GrantRequired, ex.Code);
        Assert.Contains(grants.All(), g => g.State == GrantState.AwaitingGrant);
    }

    [Fact]
    public async Task Only_one_service_holds_the_pipe()
    {
        if (!OperatingSystem.IsWindows()) return;   // FirstPipeInstance is Windows'

        var pipe = NewPipe();
        await using var first = new PipeNodeServer(() => new FakeNode(), new AdmitAll(), pipe);
        first.Start();

        await using var second = new PipeNodeServer(() => new FakeNode(), new AdmitAll(), pipe);
        Assert.ThrowsAny<Exception>(second.Start);
    }

    [Fact]
    public void Each_person_signed_in_has_their_own_pipe()
    {
        Assert.StartsWith(PipeNodeServer.BaseName + "-", PipeNodeServer.ThisPersonsPipe);
        Assert.True(PipeNodeServer.ThisPersonsPipe.Length > PipeNodeServer.BaseName.Length + 1);
    }

    [Fact]
    public async Task Nothing_answering_and_nothing_to_start_is_no_connection()
    {
        var launcher = new InProcessLauncher(() => false, installed: false);
        var connector = new PipeNodeConnector(launcher, NewPipe());

        Assert.False(await connector.IsInstalledAsync());
        Assert.Null(await connector.TryBindAsync());
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task Nothing_answering_starts_the_service_and_connects()
    {
        var pipe = NewPipe();
        var node = new FakeNode();
        PipeNodeServer? started = null;
        var launcher = new InProcessLauncher(() =>
        {
            started = new PipeNodeServer(() => node, new AdmitAll(), pipe);
            started.Start();
            return true;
        });

        using var client = await new PipeNodeConnector(launcher, pipe).TryBindAsync() as IDisposable;
        await using var _ = started!;

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(node.Me, await ((IAetherNodeClient)client!).GetTagAsync());
    }

    [Fact]
    public async Task The_apps_client_connects_again_and_keeps_listening_after_the_service_restarts()
    {
        var pipe = NewPipe();
        var node = new FakeNode();
        PipeNodeServer? server = null;
        var launcher = new InProcessLauncher(() =>
        {
            server = new PipeNodeServer(() => node, new AdmitAll(), pipe);
            server.Start();
            return true;
        });

        using var app = new BoundNodeClient(
            new PipeNodeConnector(launcher, pipe), logger: null,
            firstRetry: TimeSpan.FromMilliseconds(50), longestRetry: TimeSpan.FromMilliseconds(200));
        var ears = new Ears();
        using var subscription = app.Subscribe(ears);

        Assert.Equal(node.Me, await app.GetTagAsync());
        await WaitUntilAsync(() => node.Listening == 1);

        // AetherNetService goes — killed, updated — and comes back when the app's client starts it again.
        await server!.DisposeAsync();
        await WaitUntilAsync(() => launcher.Starts >= 2 && node.Listening >= 1);

        node.RaiseDelivered(Guid.NewGuid());
        await ears.UntilAsync(1, Within);
        Assert.Equal(node.Me, await app.GetTagAsync());

        await server.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition was never met");
            await Task.Delay(20);
        }
    }

    /// <summary>AetherNetService's own gate: none — it is a network cable.</summary>
    private sealed class AdmitAll : IGrantStore
    {
        public AppGrant Get(string appId) => new(appId, GrantState.Bound, DateTimeOffset.UtcNow);

        public AppGrant Save(AppGrant grant) => grant;

        public IReadOnlyList<AppGrant> All() => [];
    }
}

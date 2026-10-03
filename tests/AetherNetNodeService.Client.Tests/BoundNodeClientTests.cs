// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNetNodeService.Client.Tests;

/// <summary>
/// The app's connection to AetherNetService, the same on every system: it connects on first use, says "not now"
/// when the service is missing, and when a connection says the service went away it connects again — taking every
/// listener and the contacts to meet along — whatever kind of connection it was (a binder, a pipe).
/// </summary>
public class BoundNodeClientTests
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(10);

    private static AetherNetTag Parse(string value)
    {
        Assert.True(AetherNetTag.TryParse(value, out var tag));
        return tag;
    }

    private static readonly AetherNetTag Me = Parse("9BWNJ-QPXG8");

    [Fact]
    public async Task Missing_service_is_not_now_on_any_device()
    {
        using var client = new BoundNodeClient(new Connector { Installed = false });

        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => client.GetTagAsync());
        Assert.Equal(AetherNodeErrorCode.NodeUnavailable, ex.Code);
        Assert.DoesNotContain("phone", ex.Message);
    }

    [Fact]
    public async Task A_connection_that_dies_is_replaced_and_takes_the_listener_and_contacts_along()
    {
        var connector = new Connector();
        using var client = new BoundNodeClient(connector, logger: null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

        var listener = new Listener();
        using var _ = client.Subscribe(listener);
        var contacts = new[] { new NodeContact(Me, null, Mutual: true) };
        await client.MeetAsync(contacts);

        var first = await connector.NextAsync(1);
        await WaitUntilAsync(() => first.Listeners == 1);

        first.Die();

        var second = await connector.NextAsync(2);
        await WaitUntilAsync(() => second.Listeners == 1 && second.Met is not null);
        Assert.Same(contacts, second.Met);
        Assert.True(first.Disposed);

        Assert.Equal(Me, await client.GetTagAsync());
        Assert.Equal(2, connector.Made);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition was never met");
            await Task.Delay(10);
        }
    }

    private sealed class Connector : INodeConnector
    {
        private readonly List<Connection> _made = [];
        public bool Installed { get; init; } = true;

        public int Made
        {
            get
            {
                lock (_made) return _made.Count;
            }
        }

        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installed);

        public Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default)
        {
            var connection = new Connection();
            lock (_made) _made.Add(connection);
            return Task.FromResult<IAetherNodeClient?>(connection);
        }

        public async Task<Connection> NextAsync(int count)
        {
            await WaitUntilAsync(() => Made >= count);
            lock (_made) return _made[count - 1];
        }
    }

    /// <summary>One connection to the service, which the test can kill.</summary>
    private sealed class Connection : IAetherNodeClient, INodeConnection, IDisposable
    {
        private int _listeners;

        public bool IsAlive { get; private set; } = true;
        public bool Disposed { get; private set; }
        public IReadOnlyList<NodeContact>? Met { get; private set; }
        public int Listeners => Volatile.Read(ref _listeners);

        public event Action? Died;

        public void Die()
        {
            IsAlive = false;
            Died?.Invoke();
        }

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => Task.FromResult(Me);
        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 1 });
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.FromResult(data.ToArray());
        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default) => Task.FromResult(OutboundResult.Queued);
        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InboundMessage>>([]);
        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default) => Task.FromResult(NodeLinkStatus.Offline);

        public Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
        {
            Met = contacts;
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(IAetherNodeEvents listener)
        {
            Interlocked.Increment(ref _listeners);
            return new Stop(this);
        }

        public void Dispose() => Disposed = true;

        private sealed class Stop(Connection owner) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref owner._listeners);
        }
    }

    private sealed class Listener : IAetherNodeEvents
    {
        public void OnInbound(InboundMessage message) { }
        public void OnLinkChanged(NodeLinkStatus status) { }
        public void OnGrantChanged(GrantState state) { }
    }
}

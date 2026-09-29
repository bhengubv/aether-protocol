// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Identity;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// An <see cref="IAetherNodeClient"/> that connects to AetherNetService on first use and keeps the connection —
/// what a thin client app registers, so it never holds an identity or a radio of its own.
///
/// <para>
/// Nothing here blocks: every call awaits the connection, so resolving this from the container costs nothing on
/// any thread, a page's included. If the service is not installed, or cannot be reached, each call fails with
/// <see cref="AetherNodeErrorCode.NodeUnavailable"/> — never "absent", so a caller is never tempted to mint an
/// identity of its own. A failed attempt is not remembered: the next call tries again, because the service may
/// be installed, or the phone unlocked, a moment later.
/// </para>
///
/// <para>
/// If the service dies — killed for memory, crashed, updated — this connects again straight away and moves every
/// subscription onto the new connection, so messages and delivery receipts keep arriving without the app having
/// to notice anything happened.
/// </para>
/// </summary>
public sealed class BoundNodeClient : IAetherNodeClient, IDisposable
{
    private readonly INodeConnector _connector;
    private readonly object _gate = new();
    private readonly List<DeferredSubscription> _subscriptions = [];
    private Task<IAetherNodeClient>? _connecting;
    private bool _disposed;

    public BoundNodeClient(INodeConnector connector)
        => _connector = connector ?? throw new ArgumentNullException(nameof(connector));

    public async Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetTagAsync(cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SignAsync(data, cancellationToken).ConfigureAwait(false);

    public async Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SendAsync(to, payload, messageId, cancellationToken).ConfigureAwait(false);

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(to, payload, Guid.NewGuid(), cancellationToken);

    public async Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).MeetAsync(contacts, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetInboxAsync(limit, cancellationToken).ConfigureAwait(false);

    public async Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetLinkAsync(cancellationToken).ConfigureAwait(false);

    public async Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false);

    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscription = new DeferredSubscription(this, listener);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        _ = AttachAsync(subscription);
        return subscription;
    }

    public void Dispose()
    {
        Task<IAetherNodeClient>? connecting;
        lock (_gate)
        {
            _disposed = true;
            connecting = _connecting;
            _connecting = null;
        }

        if (connecting is { IsCompletedSuccessfully: true } && connecting.Result is IDisposable bound)
        {
            bound.Dispose();   // unbinds
        }
    }

    private Task<IAetherNodeClient> ClientAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromException<IAetherNodeClient>(new ObjectDisposedException(nameof(BoundNodeClient)));
            }

            if (_connecting is null || _connecting.IsFaulted || _connecting.IsCanceled
                || (_connecting.IsCompletedSuccessfully && _connecting.Result is BinderNodeClient { IsAlive: false }))
            {
                _connecting = ConnectAsync();
            }

            return _connecting;
        }
    }

    private async Task<IAetherNodeClient> ConnectAsync()
    {
        if (!await _connector.IsInstalledAsync().ConfigureAwait(false))
        {
            throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not installed on this phone");
        }

        var client = await _connector.TryBindAsync().ConfigureAwait(false)
            ?? throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService did not accept the connection");

        if (client is BinderNodeClient binder)
        {
            binder.Died += () => OnDied(binder);
        }

        return client;
    }

    /// <summary>The service died. Drop the dead connection, connect again, and move every subscription over.</summary>
    private void OnDied(BinderNodeClient dead)
    {
        DeferredSubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            if (_connecting is { IsCompletedSuccessfully: true } && ReferenceEquals(_connecting.Result, dead))
            {
                _connecting = null;
            }

            subscriptions = [.. _subscriptions];
        }

        try { dead.Dispose(); } catch { /* it is already gone */ }

        // Reconnecting through the subscriptions starts the service again (the bind creates it) and puts every
        // listener on the new connection. With no subscribers, the next call reconnects on its own.
        foreach (var subscription in subscriptions)
        {
            _ = AttachAsync(subscription);
        }
    }

    private async Task AttachAsync(DeferredSubscription subscription)
    {
        try
        {
            var client = await ClientAsync().ConfigureAwait(false);
            subscription.Attach(client);
        }
        catch (AetherNodeException)
        {
            // Not connected, so no events for now. The next call — or the next death notice — tries again.
        }
    }

    private void Forget(DeferredSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>
    /// A subscription that outlives any one connection: made before the connection exists, attached when it
    /// arrives, and moved to the next connection if the service dies.
    /// </summary>
    private sealed class DeferredSubscription(BoundNodeClient owner, IAetherNodeEvents listener) : IDisposable
    {
        private readonly object _gate = new();
        private IAetherNodeClient? _client;
        private IDisposable? _inner;
        private bool _disposed;

        public void Attach(IAetherNodeClient client)
        {
            IDisposable? previous;
            lock (_gate)
            {
                if (_disposed || ReferenceEquals(_client, client)) return;   // gone, or already on this connection
                previous = _inner;
                _inner = client.Subscribe(listener);
                _client = client;
            }

            try { previous?.Dispose(); } catch { /* the old connection is dead */ }
        }

        public void Dispose()
        {
            IDisposable? inner;
            lock (_gate)
            {
                _disposed = true;
                inner = _inner;
                _inner = null;
                _client = null;
            }

            owner.Forget(this);
            try { inner?.Dispose(); } catch { /* the connection is already gone */ }
        }
    }
}
#endif

// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Identity;
using AetherNet.Node.Client;

namespace AetherNet.Node.Android;

/// <summary>
/// An <see cref="IAetherNodeClient"/> that connects to AetherNetService on first use and keeps the one
/// connection — what a thin client app registers, so it never holds an identity or a radio of its own.
///
/// <para>
/// Nothing here blocks: every call awaits the connection, so resolving this from the container costs nothing
/// on any thread, a page's included. If the service is not installed, or cannot be reached, each call fails
/// with <see cref="AetherNodeErrorCode.NodeUnavailable"/> — never "absent", so a caller is never tempted to
/// mint an identity of its own. A failed attempt is not remembered: the next call tries again, because the
/// service may be installed, or the phone unlocked, a moment later.
/// </para>
/// </summary>
public sealed class BoundNodeClient : IAetherNodeClient, IDisposable
{
    private readonly INodeConnector _connector;
    private readonly object _gate = new();
    private Task<IAetherNodeClient>? _connecting;

    public BoundNodeClient(INodeConnector connector)
        => _connector = connector ?? throw new ArgumentNullException(nameof(connector));

    public async Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetTagAsync(cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SignAsync(data, cancellationToken).ConfigureAwait(false);

    public async Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SendAsync(to, payload, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetInboxAsync(limit, cancellationToken).ConfigureAwait(false);

    public async Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetLinkAsync(cancellationToken).ConfigureAwait(false);

    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscription = new DeferredSubscription();
        _ = AttachAsync();
        return subscription;

        async Task AttachAsync()
        {
            try
            {
                var client = await ClientAsync().ConfigureAwait(false);
                subscription.Attach(client.Subscribe(listener));
            }
            catch (AetherNodeException)
            {
                // Not connected, so no events. The caller's next request surfaces why.
            }
        }
    }

    public void Dispose()
    {
        Task<IAetherNodeClient>? connecting;
        lock (_gate)
        {
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
            if (_connecting is null || _connecting.IsFaulted || _connecting.IsCanceled)
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

        var client = await _connector.TryBindAsync().ConfigureAwait(false);
        return client ?? throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService did not accept the connection");
    }

    /// <summary>A subscription made before the connection exists; it attaches when the connection arrives.</summary>
    private sealed class DeferredSubscription : IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _inner;
        private bool _disposed;

        public void Attach(IDisposable inner)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _inner = inner;
                    return;
                }
            }

            inner.Dispose();   // unsubscribed before the connection arrived
        }

        public void Dispose()
        {
            IDisposable? inner;
            lock (_gate)
            {
                _disposed = true;
                inner = _inner;
                _inner = null;
            }

            inner?.Dispose();
        }
    }
}
#endif

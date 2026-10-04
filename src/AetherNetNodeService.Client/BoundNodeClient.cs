// SPDX-License-Identifier: MIT
using System.Diagnostics;
using AetherNet.Identity;
using Microsoft.Extensions.Logging;

namespace AetherNetNodeService.Client;

/// <summary>
/// An <see cref="IAetherNodeClient"/> that connects to AetherNetService on first use and keeps the connection —
/// what a thin client app registers, so it never holds an identity or a radio of its own. The same on every system:
/// only the <see cref="INodeConnector"/> differs (a bind on a phone, a named pipe on a computer), and any connection
/// that is an <see cref="INodeConnection"/> is watched, so this connects again when the service goes.
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
/// If the service dies — killed for memory, crashed, updated — this connects again straight away, and keeps trying
/// while it cannot: an update leaves the service briefly not installed at all. Every new connection takes every
/// subscription with it and tells the service whom to meet, so messages and delivery receipts keep arriving — and
/// the radios keep meeting the right people — without the app having to notice anything happened.
/// </para>
/// </summary>
public sealed class BoundNodeClient : IAetherNodeClient, IDisposable
{
    /// <summary>The first wait before trying again to reach a service that is not there; it doubles each time.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between tries.</summary>
    private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(30);

    /// <summary>How long a new connection waits for the service to take the contacts before getting on without.</summary>
    private static readonly TimeSpan TellWithin = TimeSpan.FromSeconds(10);

    /// <summary>How long a connection has to last for a later death to count as the service dying, not failing to start.</summary>
    private static readonly TimeSpan StayedUp = TimeSpan.FromSeconds(30);

    private readonly INodeConnector _connector;
    private readonly ILogger? _logger;
    private readonly TimeSpan _firstRetry;
    private readonly TimeSpan _longestRetry;
    private readonly object _gate = new();
    private readonly List<DeferredSubscription> _subscriptions = [];
    private Task<IAetherNodeClient>? _connecting;

    /// <summary>
    /// The contacts the app last handed over. The service keeps no address book of its own, so one that starts
    /// again is told them here — otherwise it met nobody, and everything held for somebody stayed held.
    /// </summary>
    private IReadOnlyList<NodeContact>? _contacts;

    private bool _retrying;

    /// <summary>The service died again while a retry loop was running: that loop goes round once more.</summary>
    private bool _again;

    /// <summary>The wait before the next try; it keeps growing while the service dies as it starts.</summary>
    private TimeSpan _wait;

    /// <summary>When a connection last succeeded (a <see cref="Stopwatch"/> timestamp), or 0 before the first.</summary>
    private long _reachedAt;

    private bool _disposed;

    /// <param name="connector">How this system reaches AetherNetService.</param>
    /// <param name="logger">
    /// Where it says what it is doing — that the service went away, how many tries it took to reach it again. Give it
    /// one that reaches the system's own log (logcat on a phone).
    /// </param>
    public BoundNodeClient(INodeConnector connector, ILogger? logger = null)
        : this(connector, logger, FirstRetry, LongestRetry)
    {
    }

    /// <summary>With its own waits between tries — for tests, which cannot wait seconds.</summary>
    internal BoundNodeClient(INodeConnector connector, ILogger? logger, TimeSpan firstRetry, TimeSpan longestRetry)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _logger = logger;
        _firstRetry = firstRetry;
        _longestRetry = longestRetry;
        _wait = firstRetry;
    }

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
    {
        ArgumentNullException.ThrowIfNull(contacts);
        lock (_gate)
        {
            _contacts = contacts;   // kept for the next connection, whenever that is
        }

        await (await ClientAsync().ConfigureAwait(false)).MeetAsync(contacts, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetInboxAsync(limit, cancellationToken).ConfigureAwait(false);

    public async Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetLinkAsync(cancellationToken).ConfigureAwait(false);

    public async Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false);

    // AetherNetService restarts to apply it; this client reconnects by itself, as it does after an update.
    public async Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SetNearbyAsync(on, cancellationToken).ConfigureAwait(false);

    // The same for one radio.
    public async Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SetRadioAsync(radio, on, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<HelpReport> GetHelpAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).GetHelpAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> StartHelpAsync(HelpKind kind, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).StartHelpAsync(kind, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task MarkSafeAsync(CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).MarkSafeAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetHelpGuardiansAsync(IReadOnlyList<HelpGuardian> guardians, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SetHelpGuardiansAsync(guardians, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SetHelpOptionsAsync(HelpTriggers triggers, HelpAdvertForm advert, CancellationToken cancellationToken = default)
        => await (await ClientAsync().ConfigureAwait(false)).SetHelpOptionsAsync(triggers, advert, cancellationToken).ConfigureAwait(false);

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
                || (_connecting.IsCompletedSuccessfully && _connecting.Result is INodeConnection { IsAlive: false }))
            {
                // Off this lock and off the caller's thread: connecting binds, subscribes and calls the service,
                // none of which should happen while holding the lock every other call waits on.
                _connecting = Task.Run(ConnectAsync);
            }

            return _connecting;
        }
    }

    private async Task<IAetherNodeClient> ConnectAsync()
    {
        if (!await _connector.IsInstalledAsync().ConfigureAwait(false))
        {
            throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not installed on this device");
        }

        var client = await _connector.TryBindAsync().ConfigureAwait(false)
            ?? throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService did not accept the connection");

        if (client is INodeConnection connection)
        {
            connection.Died += () => OnDied(client);

            // Died in the moment between connecting and listening for its death — what an update can do. A connection
            // that is already dead is a failed attempt, not a connection.
            if (!connection.IsAlive)
            {
                (client as IDisposable)?.Dispose();
                throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService went away as it connected");
            }
        }

        // However this connection came about — a call, a subscription, the service coming back — it takes every
        // listener along and tells the service whom to meet. Only a death notice used to move the listeners, so a
        // connection made by an ordinary call left the app deaf; and a service that had started again was never
        // told whom to meet, so nothing it held for anybody went out until the app was restarted.
        AttachAll(client);
        await TellContactsAsync(client).ConfigureAwait(false);

        // Died while it was being told whom to meet — a service dying as it starts. Not a connection.
        if (client is INodeConnection { IsAlive: false })
        {
            (client as IDisposable)?.Dispose();
            throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService went away as it connected");
        }

        lock (_gate)
        {
            _reachedAt = Stopwatch.GetTimestamp();
        }

        return client;
    }

    private void AttachAll(IAetherNodeClient client)
    {
        DeferredSubscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions];
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Attach(client);
        }
    }

    private async Task TellContactsAsync(IAetherNodeClient client)
    {
        IReadOnlyList<NodeContact>? contacts;
        lock (_gate)
        {
            contacts = _contacts;
        }

        if (contacts is null) return;   // the app has not handed any over yet; it will

        try
        {
            // Bounded: every call waits on this connection, and none of them should wait on the radios.
            await client.MeetAsync(contacts).WaitAsync(TellWithin).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The connection is good for everything else; the app's next contact change sends them again.
            _logger?.LogWarning("connected, but could not tell AetherNetService whom to meet: {Reason}", ex.Message);
        }
    }

    /// <summary>The service died. Drop the dead connection and connect again.</summary>
    private void OnDied(IAetherNodeClient dead)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_connecting is { IsCompletedSuccessfully: true } && ReferenceEquals(_connecting.Result, dead))
            {
                _connecting = null;
            }
        }

        try { (dead as IDisposable)?.Dispose(); } catch { /* it is already gone */ }

        _logger?.LogInformation("AetherNetService went away — connecting again");

        // Connecting starts the service again, and the new connection takes every listener with it. With nobody
        // listening, the next call reconnects on its own.
        _ = Task.Run(KeepTryingAsync);
    }

    private async Task AttachAsync(DeferredSubscription subscription)
    {
        try
        {
            subscription.Attach(await ClientAsync().ConfigureAwait(false));
        }
        catch (Exception)
        {
            // Not reachable right now. Keep trying in the background; the connection that comes of it takes this
            // listener along.
            _ = Task.Run(KeepTryingAsync);
        }
    }

    /// <summary>
    /// Keep trying to reach the service while anything is listening, waiting a second, then two, then four — up to
    /// half a minute — between tries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One try used to be all there was. While the service is being updated it is briefly not installed at all, so
    /// that try failed, and the app heard nothing more — no messages, no receipts — until it was restarted.
    /// </para>
    /// <para>
    /// A death notice that arrives while a loop is already running is that loop's to deal with: it goes round again
    /// rather than stopping on a connection that has just died. That notice used to be dropped, and after a service
    /// died four times in a row as it started (Pixel, 2026-09-30) nothing was trying any more.
    /// </para>
    /// </remarks>
    private async Task KeepTryingAsync()
    {
        bool waitFirst;
        lock (_gate)
        {
            if (_disposed || _subscriptions.Count == 0) return;
            if (_retrying)
            {
                _again = true;   // the loop already running takes this death too
                return;
            }

            _retrying = true;
            _again = false;

            // A service that died soon after it was reached is dying as it starts, and reconnecting at once only starts
            // it to die again — so the wait carries on growing. One that stayed up starts the waits over.
            waitFirst = _reachedAt != 0 && Stopwatch.GetElapsedTime(_reachedAt) < StayedUp;
            if (!waitFirst) _wait = _firstRetry;
        }

        for (var tries = 1; ; tries++)
        {
            if (waitFirst || tries > 1)
            {
                await Task.Delay(NextWait()).ConfigureAwait(false);
            }

            var reached = false;
            try
            {
                var client = await ClientAsync().ConfigureAwait(false);
                if (client is INodeConnection { IsAlive: false })
                {
                    throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService went away as it connected");
                }

                AttachAll(client);
                reached = true;
                if (tries > 1) _logger?.LogInformation("reached AetherNetService again after {Tries} tries", tries);
            }
            catch (Exception ex)
            {
                if (tries == 1) _logger?.LogInformation("AetherNetService not reachable yet ({Reason}) — trying again", ex.Message);
            }

            lock (_gate)
            {
                // Stop when nobody is left to reach it for, or when it is reached and nothing has died since — decided
                // under the lock a death notice takes, so no notice can land between the decision and letting go.
                if (_disposed || _subscriptions.Count == 0 || (reached && !_again))
                {
                    _retrying = false;
                    return;
                }

                _again = false;
            }
        }
    }

    /// <summary>The wait before the next try: it doubles each time, up to the longest wait.</summary>
    private TimeSpan NextWait()
    {
        lock (_gate)
        {
            var wait = _wait;
            _wait = wait * 2 < _longestRetry ? wait * 2 : _longestRetry;
            return wait;
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

                IDisposable inner;
                try
                {
                    inner = client.Subscribe(listener);
                }
                catch (Exception)
                {
                    return;   // that connection died under us; its death notice brings the next one, and this along
                }

                previous = _inner;
                _inner = inner;
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

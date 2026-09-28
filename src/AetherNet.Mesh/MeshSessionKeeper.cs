// SPDX-License-Identifier: MIT

using AetherNet.Messaging;
using AetherNet.PreKeys;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Mesh;

/// <summary>
/// Keeps a secure session with every peer this node talks to — the half of a messenger that belongs to the
/// node rather than to the app. The reliable core seals and sends but holds no pre-keys, so it asks for help
/// twice: <see cref="IMessagingService.SessionRequired"/> when a message cannot go because there is no
/// session yet, and <see cref="IMessagingService.DecryptFailed"/> when one arrives that will not open. This
/// answers both: it publishes this node's pre-key bundle, asks a peer for theirs, builds the session when it
/// arrives, and rebuilds a session that has diverged.
///
/// <para>
/// Lifted from the chat app, where it used to live, so that every app connected to the node shares one
/// session per peer instead of each building its own. <see cref="SessionEstablished"/> tells whoever is
/// holding messages for that peer that they can go now.
/// </para>
/// </summary>
public sealed class MeshSessionKeeper : IDisposable
{
    private readonly IIdentityService _me;
    private readonly ISignalProtocolService _signal;
    private readonly IPreKeyExchangeService _preKeys;
    private readonly IMessagingService _messaging;
    private readonly ILogger _log;
    private readonly SessionRepair _repair = new();
    private readonly SemaphoreSlim _bundleGate = new(1, 1);
    private volatile bool _bundlePublished;

    public MeshSessionKeeper(IIdentityService me, ISignalProtocolService signal, IPreKeyExchangeService preKeys,
        IMessagingService messaging, ILogger<MeshSessionKeeper>? logger = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _preKeys = preKeys ?? throw new ArgumentNullException(nameof(preKeys));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _log = (ILogger?)logger ?? NullLogger.Instance;

        _preKeys.BundleReceived += OnBundleReceived;
        _messaging.SessionRequired += OnSessionRequired;
        _messaging.DecryptFailed += OnDecryptFailed;
    }

    /// <summary>A session with this peer has just been built — anything held for them can go now.</summary>
    public event Action<string>? SessionEstablished;

    /// <summary>Is there a secure session with this peer?</summary>
    public bool HasSession(string peerTag) => !string.IsNullOrEmpty(peerTag) && _signal.HasSession(peerTag);

    /// <summary>
    /// A pre-key request or response arrived over the radio. Our own bundle has to exist before a request for
    /// it can be answered, so it is published first.
    /// </summary>
    public async Task HandlePreKeyAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        await EnsureLocalBundleAsync(cancellationToken).ConfigureAwait(false);
        await _preKeys.HandleAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publish this node's bundle, and if there is no session with the peer, get one started.</summary>
    public async Task EnsureSessionAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(peerTag)) return;

        await EnsureLocalBundleAsync(cancellationToken).ConfigureAwait(false);
        if (_signal.HasSession(peerTag)) return;

        // Their bundle may already have arrived unasked; otherwise ask for it over the radio.
        var known = _preKeys.GetReceivedBundle(peerTag);
        if (known is not null)
        {
            await AdoptAsync(peerTag, known, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _preKeys.RequestBundleAsync(peerTag, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish this node's bundle once. A node must have one ready <b>before</b> anyone asks, or it cannot
    /// answer a request and no session can ever start.
    /// </summary>
    public Task EnsureLocalBundleAsync(CancellationToken cancellationToken = default)
        => _bundlePublished ? Task.CompletedTask : RefreshLocalBundleAsync(cancellationToken);

    /// <summary>
    /// A message from this peer would not open: the two ratchets have diverged. Drop the session, publish a
    /// fresh bundle — the peer used up our one-time key building the session that just died, and offering it
    /// again is refused — and ask for theirs. At most once per <see cref="SessionRepair.Cooldown"/> per peer.
    /// </summary>
    public async Task RepairAsync(string peerTag)
    {
        if (!_repair.ShouldRestart(peerTag, DateTime.UtcNow)) return;

        _log.LogWarning("Could not read a message from {Peer}: the session has diverged; rebuilding it", peerTag);
        _signal.DropSession(peerTag);
        await RefreshLocalBundleAsync().ConfigureAwait(false);
        await _preKeys.RequestBundleAsync(peerTag).ConfigureAwait(false);
    }

    private async Task RefreshLocalBundleAsync(CancellationToken cancellationToken = default)
    {
        await _bundleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bundle = await _signal.GeneratePreKeyBundleAsync(_me.AetherTag, cancellationToken).ConfigureAwait(false);
            _preKeys.SetLocalBundle(bundle);
            _bundlePublished = true;
        }
        finally
        {
            _bundleGate.Release();
        }
    }

    private async Task AdoptAsync(string peerTag, AetherNet.Security.Models.PreKeyBundle bundle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(peerTag) || _signal.HasSession(peerTag)) return;

        await _signal.ProcessPreKeyBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
        _repair.Forget(peerTag);
        _log.LogInformation("Secure session with {Peer} established", peerTag);
        SessionEstablished?.Invoke(peerTag);
    }

    private void OnSessionRequired(object? sender, string peer)
        => _ = RunAsync(() => EnsureSessionAsync(peer), "start a session", peer);

    private void OnDecryptFailed(object? sender, string peer)
        => _ = RunAsync(() => RepairAsync(peer), "repair the session", peer);

    private void OnBundleReceived(object? sender, PreKeyBundleReceivedEventArgs e)
    {
        if (e.Bundle is null) return;
        var peer = !string.IsNullOrEmpty(e.FromUhid) ? e.FromUhid : e.Bundle.Uhid;
        _ = RunAsync(() => AdoptAsync(peer, e.Bundle, CancellationToken.None), "adopt the bundle", peer);
    }

    // Every trigger here is an event with nobody to await it; a failure is logged, never thrown into the void.
    private async Task RunAsync(Func<Task> work, string what, string peer)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not {What} with {Peer}", what, peer);
        }
    }

    public void Dispose()
    {
        _preKeys.BundleReceived -= OnBundleReceived;
        _messaging.SessionRequired -= OnSessionRequired;
        _messaging.DecryptFailed -= OnDecryptFailed;
    }
}

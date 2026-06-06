// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherMesh.Transport.Abstractions;

namespace AetherMesh.Transport.Services;

/// <summary>
/// In-process simulation of a Wi-Fi Direct transport. Two or more instances registered with
/// different UHIDs can establish logical connections and exchange large payloads without
/// framing (Wi-Fi Direct supports up to 64 KB per
/// <see cref="AetherMesh.Constants.ProtocolConstants.WifiDirectMaxPayloadBytes"/>).
///
/// A connection is a symmetric, bilateral relationship. Calling
/// <see cref="ConnectAsync"/> on node A both marks B as connected from A's perspective
/// <em>and</em> fires <see cref="PeerConnected"/> on B.
///
/// Usage in tests:
/// <code>
/// using var nodeA = new SimulatedWifiDirectTransportService("uhid-a");
/// using var nodeB = new SimulatedWifiDirectTransportService("uhid-b");
/// await nodeA.ConnectAsync("uhid-b");
/// nodeB.DataReceived += (sender, data) => { /* ... */ };
/// await nodeA.SendAsync("uhid-b", payload);
/// </code>
/// </summary>
public sealed class SimulatedWifiDirectTransportService : IWifiDirectService, IDisposable
{
    // ── Static registry ──────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, SimulatedWifiDirectTransportService> Registry = new();

    /// <summary>Removes all nodes from the registry. Useful for test setup/teardown.</summary>
    public static void ResetRegistry() => Registry.Clear();

    // ── Instance state ───────────────────────────────────────────────────────
    private readonly string _localUhid;
    private readonly ConcurrentDictionary<string, bool> _connectedPeers = new();
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new simulated Wi-Fi Direct node and registers it in the in-process network.
    /// </summary>
    /// <param name="localUhid">The UHID of this node. Must be unique across all live instances.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a node with <paramref name="localUhid"/> is already registered.
    /// </exception>
    public SimulatedWifiDirectTransportService(string localUhid)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));

        if (!Registry.TryAdd(localUhid, this))
            throw new InvalidOperationException(
                $"A SimulatedWifiDirectTransportService with UHID '{localUhid}' is already registered. " +
                "Dispose the existing instance first.");
    }

    // ── ITransportService ────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "WiFi-Direct-Sim";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 250_000_000;

    /// <inheritdoc />
    public int MaxRangeMeters => 200;

    /// <inheritdoc />
    public int PowerCostRelative => 5;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 8;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    // ── IWifiDirectService ───────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action<string>? PeerConnected;

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected;

    /// <summary>
    /// Establishes a logical Wi-Fi Direct connection to <paramref name="peerUhid"/>.
    /// Adds the peer to the local connected set and fires <see cref="PeerConnected"/> on
    /// both this node and the peer.
    /// </summary>
    /// <returns><c>true</c> when the peer is registered; <c>false</c> otherwise.</returns>
    public Task<bool> ConnectAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Registry.TryGetValue(peerUhid, out var peer) || peer._disposed)
            return Task.FromResult(false);

        _connectedPeers.TryAdd(peerUhid, true);
        peer._connectedPeers.TryAdd(_localUhid, true);

        // Notify both sides.
        PeerConnected?.Invoke(peerUhid);
        peer.PeerConnected?.Invoke(_localUhid);

        return Task.FromResult(true);
    }

    /// <summary>
    /// Tears down the logical connection to <paramref name="peerUhid"/>.
    /// Fires <see cref="PeerDisconnected"/> on both sides.
    /// </summary>
    public Task DisconnectAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _connectedPeers.TryRemove(peerUhid, out _);

        if (Registry.TryGetValue(peerUhid, out var peer) && !peer._disposed)
        {
            peer._connectedPeers.TryRemove(_localUhid, out _);
            peer.PeerDisconnected?.Invoke(_localUhid);
        }

        PeerDisconnected?.Invoke(peerUhid);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends <paramref name="data"/> to <paramref name="peerUhid"/> without framing.
    /// Wi-Fi Direct handles large payloads natively — no chunking is applied.
    /// </summary>
    /// <returns><c>false</c> when not connected to the peer.</returns>
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connectedPeers.ContainsKey(peerUhid))
            return Task.FromResult(false);

        if (!Registry.TryGetValue(peerUhid, out var peer) || peer._disposed)
            return Task.FromResult(false);

        var copy = new byte[data.Length];
        Buffer.BlockCopy(data, 0, copy, 0, data.Length);

        peer.DataReceived?.Invoke(_localUhid, copy);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to completion and sends the bytes via
    /// <see cref="SendAsync"/>.
    /// </summary>
    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="peerUhid"/> is in the local connected set.
    /// </summary>
    public bool IsConnected(string peerUhid)
        => !_disposed && _connectedPeers.ContainsKey(peerUhid);

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes this node from the registry, clears connections, and clears event subscriptions.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Registry.TryRemove(_localUhid, out _);
        _connectedPeers.Clear();
        DataReceived = null;
        PeerConnected = null;
        PeerDisconnected = null;
    }
}

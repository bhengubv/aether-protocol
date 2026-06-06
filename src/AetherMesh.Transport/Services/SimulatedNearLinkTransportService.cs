// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherMesh.Transport.NearLink;

namespace AetherMesh.Transport.Services;

/// <summary>
/// In-process simulation of a NearLink transport. NearLink has a larger MTU than BLE
/// (<see cref="AetherMesh.Constants.ProtocolConstants.NearLinkMaxPayloadBytes"/> = 4096 bytes)
/// and can maintain up to 500 concurrent peers.
///
/// Large payloads are split into 4096-byte frames and reassembled at the receiver, mirroring
/// the same chunked-delivery semantics the physical NearLink stack would use.
///
/// "Connected" in simulation means: peer is present in the registry (no explicit handshake
/// required — NearLink auto-discovers peers in range). <see cref="PeerConnected"/> fires
/// when the remote side is first encountered via <see cref="SendAsync"/> or when the remote
/// side sends to us.
///
/// Usage in tests:
/// <code>
/// using var nodeA = new SimulatedNearLinkTransportService("uhid-a");
/// using var nodeB = new SimulatedNearLinkTransportService("uhid-b");
/// nodeB.DataReceived += (sender, data) => { /* ... */ };
/// await nodeA.SendAsync("uhid-b", payload);
/// </code>
/// </summary>
public sealed class SimulatedNearLinkTransportService : INearLinkTransportService, IDisposable
{
    // ── Static registry ──────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, SimulatedNearLinkTransportService> Registry = new();

    /// <summary>Removes all nodes from the registry. Useful for test setup/teardown.</summary>
    public static void ResetRegistry() => Registry.Clear();

    // ── Instance state ───────────────────────────────────────────────────────
    private readonly string _localUhid;
    private volatile bool _disposed;

    private const int NearLinkMtu = 4096;
    private const int FrameHeaderSize = 4; // 2 bytes frame_count + 2 bytes frame_index

    /// <summary>
    /// Creates a new simulated NearLink node and registers it in the in-process network.
    /// </summary>
    /// <param name="localUhid">The UHID of this node. Must be unique across all live instances.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a node with <paramref name="localUhid"/> is already registered.
    /// </exception>
    public SimulatedNearLinkTransportService(string localUhid)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));

        if (!Registry.TryAdd(localUhid, this))
            throw new InvalidOperationException(
                $"A SimulatedNearLinkTransportService with UHID '{localUhid}' is already registered. " +
                "Dispose the existing instance first.");
    }

    // ── INearLinkTransportService ────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "NearLink-Sim";

    /// <inheritdoc />
    public bool IsAvailable { get; set; } = true;

    /// <inheritdoc />
    public long MaxBandwidthBps => 12_000_000;

    /// <inheritdoc />
    public int MaxRangeMeters => 600;

    /// <inheritdoc />
    public int PowerCostRelative => 1;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 500;

    /// <summary>
    /// Number of currently registered peers in the network (excluding this node).
    /// </summary>
    public int ConnectedPeerCount
    {
        get
        {
            if (_disposed) return 0;
            int count = 0;
            foreach (var (uhid, peer) in Registry)
            {
                if (uhid != _localUhid && !peer._disposed)
                    count++;
            }
            return count;
        }
    }

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

#pragma warning disable CS0414 // events are part of INearLinkTransportService; unused in simulation
    /// <inheritdoc />
    public event Action<string>? PeerConnected;

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected;
#pragma warning restore CS0414

    // ── Send ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="data"/> to <paramref name="peerUhid"/>.
    /// Large payloads are split into 4096-byte NearLink frames and reassembled before
    /// the peer's <see cref="DataReceived"/> event fires.
    /// </summary>
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.FromResult(false);
        ArgumentNullException.ThrowIfNull(data);

        if (!Registry.TryGetValue(peerUhid, out var peer) || peer._disposed)
            return Task.FromResult(false);

        // Frame and reassemble using BleGattFramer-compatible simple framing at NearLink MTU.
        var frames = BleGattFramer.Frame(data, NearLinkMtu);
        var frameList = new List<byte[]>(frames);
        var reassembled = BleGattFramer.Reassemble(frameList);

        if (reassembled is null)
            return Task.FromResult(false);

        var copy = new byte[reassembled.Length];
        Buffer.BlockCopy(reassembled, 0, copy, 0, reassembled.Length);

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
        if (_disposed) return false;
        ArgumentNullException.ThrowIfNull(stream);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="peerUhid"/> is registered and not disposed.
    /// NearLink auto-associates with all nodes in range; the registry serves as the
    /// simulated "range" boundary.
    /// </summary>
    public bool IsConnected(string peerUhid)
        => !_disposed
           && !string.IsNullOrEmpty(peerUhid)
           && Registry.TryGetValue(peerUhid, out var peer)
           && !peer._disposed;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes this node from the registry and clears all event subscriptions.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Registry.TryRemove(_localUhid, out _);
        DataReceived = null;
        PeerConnected = null;
        PeerDisconnected = null;
    }
}

// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Models;

namespace AetherNet.Transport.Services;

/// <summary>
/// In-process simulation of a BLE GATT transport. Two or more instances registered with
/// different UHIDs can communicate directly in memory, using <see cref="BleGattFramer"/> to
/// split large payloads into MTU-sized chunks exactly as real BLE GATT hardware would.
///
/// Usage in tests:
/// <code>
/// using var nodeA = new SimulatedBleGattTransportService("uhid-a");
/// using var nodeB = new SimulatedBleGattTransportService("uhid-b");
/// nodeB.DataReceived += (sender, data) => { /* ... */ };
/// await nodeA.SendAsync("uhid-b", payload);
/// </code>
/// </summary>
public sealed class SimulatedBleGattTransportService : IBleTransportService, IDisposable
{
    // ── Static registry ──────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, SimulatedBleGattTransportService> Registry = new();

    /// <summary>Removes all nodes from the registry. Useful for test setup/teardown.</summary>
    public static void ResetRegistry() => Registry.Clear();

    // ── Instance state ───────────────────────────────────────────────────────
    private readonly string _localUhid;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new simulated BLE GATT node and registers it in the in-process network.
    /// </summary>
    /// <param name="localUhid">The UHID of this node. Must be unique across all live instances.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a node with <paramref name="localUhid"/> is already registered.
    /// Dispose the existing instance first.
    /// </exception>
    public SimulatedBleGattTransportService(string localUhid)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));

        if (!Registry.TryAdd(localUhid, this))
            throw new InvalidOperationException(
                $"A SimulatedBleGattTransportService with UHID '{localUhid}' is already registered. " +
                "Dispose the existing instance first.");
    }

    // ── ITransportService ────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "BLE-GATT-Sim";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 2_000_000;

    /// <inheritdoc />
    public int MaxRangeMeters => 100;

    /// <inheritdoc />
    public int PowerCostRelative => 2;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 7;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    /// <inheritdoc />
    public event Action<BleAdvertisement>? AdvertisementReceived;

    // ── IBleTransportService ─────────────────────────────────────────────────

    /// <summary>
    /// Broadcasts a BLE advertisement to every registered peer except this node.
    /// Each peer's <see cref="AdvertisementReceived"/> event is fired synchronously.
    /// </summary>
    public Task<bool> SendAdvertisementAsync(BleAdvertisement advertisement,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(advertisement);

        foreach (var (uhid, peer) in Registry)
        {
            if (uhid == _localUhid || peer._disposed)
                continue;

            peer.AdvertisementReceived?.Invoke(advertisement);
        }

        return Task.FromResult(true);
    }

    // ── ITransportService ────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="data"/> to <paramref name="peerUhid"/> using GATT framing.
    /// Large payloads are chunked into 1024-byte frames; the peer reassembles them before
    /// firing <see cref="DataReceived"/>.
    /// </summary>
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Registry.TryGetValue(peerUhid, out var peer) || peer._disposed)
            return Task.FromResult(false);

        // Chunk via GATT framer.
        var frames = BleGattFramer.Frame(data);

        // Accumulate frames and deliver reassembled data to the peer.
        var frameList = new List<byte[]>(frames);
        var reassembled = BleGattFramer.Reassemble(frameList);

        if (reassembled is null)
            return Task.FromResult(false);

        // Defensive copy so the receiver cannot mutate the sender's buffer.
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="peerUhid"/> is registered and not disposed.
    /// BLE GATT in simulation is "connected" simply by being in the same registry.
    /// </summary>
    public bool IsConnected(string peerUhid)
        => !_disposed
           && !string.IsNullOrEmpty(peerUhid)
           && Registry.TryGetValue(peerUhid, out var peer)
           && !peer._disposed;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>Removes this node from the registry and clears all event subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Registry.TryRemove(_localUhid, out _);
        DataReceived = null;
        AdvertisementReceived = null;
    }
}

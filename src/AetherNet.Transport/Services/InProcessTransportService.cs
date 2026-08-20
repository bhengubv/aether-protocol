// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Services;

/// <summary>
/// In-memory transport for testing and demos. Simulates a network of nodes using a static
/// registry. Each instance represents one node; sending data to a peer delivers it directly
/// to that peer's <see cref="DataReceived"/> event via the in-process registry.
///
/// Usage:
/// <code>
/// var nodeA = new InProcessTransportService("uhid-a", logger);
/// var nodeB = new InProcessTransportService("uhid-b", logger);
/// nodeB.DataReceived += (sender, data) => Console.WriteLine($"B received from {sender}");
/// await nodeA.SendAsync("uhid-b", payload);
/// </code>
/// </summary>
public sealed class InProcessTransportService : ITransportService, IDisposable
{
    private static readonly ConcurrentDictionary<string, InProcessTransportService> Network = new();

    private readonly string _localUhid;
    private readonly ILogger<InProcessTransportService> _logger;
    private bool _disposed;

    /// <summary>
    /// Creates a new in-process transport node and registers it in the simulated network.
    /// </summary>
    /// <param name="localUhid">The UHID of this node.</param>
    /// <param name="logger">Logger instance.</param>
    public InProcessTransportService(string localUhid, ILogger<InProcessTransportService> logger)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!Network.TryAdd(localUhid, this))
        {
            throw new InvalidOperationException(
                $"An InProcessTransportService with UHID '{localUhid}' is already registered. " +
                "Dispose the existing instance first or use a different UHID.");
        }

        _logger.LogInformation("InProcess node '{Uhid}' joined the simulated network ({Count} nodes total)",
            localUhid, Network.Count);
    }

    /// <inheritdoc />
    public string Name => "InProcess";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 1_000_000_000; // 1 Gbps — in-memory, effectively unlimited

    /// <inheritdoc />
    public int MaxRangeMeters => 0; // Not applicable — in-process

    /// <inheritdoc />
    public int PowerCostRelative => 0; // No power cost

    /// <inheritdoc />
    public int MaxConcurrentPeers => int.MaxValue;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    /// <inheritdoc />
    public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(peerUhid))
        {
            _logger.LogWarning("SendAsync called with empty peer UHID");
            return Task.FromResult(false);
        }

        if (!Network.TryGetValue(peerUhid, out var targetNode))
        {
            _logger.LogDebug("Peer '{Peer}' not found in simulated network", peerUhid);
            return Task.FromResult(false);
        }

        if (targetNode._disposed)
        {
            _logger.LogDebug("Peer '{Peer}' is disposed", peerUhid);
            return Task.FromResult(false);
        }

        // Deliver data to the target node's DataReceived event
        // Copy the data to prevent mutation after send
        var dataCopy = new byte[data.Length];
        Buffer.BlockCopy(data, 0, dataCopy, 0, data.Length);

        try
        {
            targetNode.DataReceived?.Invoke(_localUhid, dataCopy);
            _logger.LogDebug("Delivered {Bytes} bytes from '{Source}' to '{Target}'",
                data.Length, _localUhid, peerUhid);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error delivering data from '{Source}' to '{Target}'",
                _localUhid, peerUhid);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read the entire stream into a byte array and send it
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public bool IsConnected(string peerUhid)
    {
        return !_disposed
               && !string.IsNullOrEmpty(peerUhid)
               && Network.TryGetValue(peerUhid, out var peer)
               && !peer._disposed;
    }

    /// <summary>
    /// Returns the number of active nodes in the simulated network.
    /// </summary>
    public static int ActiveNodeCount => Network.Count;

    /// <summary>
    /// Removes all nodes from the simulated network. Useful for test cleanup.
    /// </summary>
    public static void ResetNetwork()
    {
        Network.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Network.TryRemove(_localUhid, out _);
        DataReceived = null;

        _logger.LogInformation("InProcess node '{Uhid}' left the simulated network ({Count} nodes remaining)",
            _localUhid, Network.Count);
    }
}

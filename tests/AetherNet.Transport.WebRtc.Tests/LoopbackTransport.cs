// SPDX-License-Identifier: MIT

using AetherNet.Transport.Abstractions;

namespace AetherNet.Transport.WebRtc.Tests;

/// <summary>
/// Minimal in-process <see cref="ITransportService"/> that delivers everything it sends to its
/// paired instance — a stand-in for the QUIC/HTTP relay so the signalling adapter can be exercised
/// over a real <see cref="ITransportService"/> seam without a network.
/// </summary>
internal sealed class LoopbackTransport : ITransportService
{
    private readonly string _localUhid;

    public LoopbackTransport(string localUhid) => _localUhid = localUhid;

    /// <summary>The far end. Set once on both instances to wire the pair together.</summary>
    public LoopbackTransport? Peer { get; set; }

    public string Name => "Loopback";
    public bool IsAvailable => true;
    public long MaxBandwidthBps => long.MaxValue;
    public int MaxRangeMeters => 0;
    public int PowerCostRelative => 100;
    public int MaxConcurrentPeers => 2;

    public event Action<string, byte[]>? DataReceived;

    public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        var peer = Peer;
        if (peer is null) return Task.FromResult(false);
        peer.Receive(_localUhid, data); // ordered, reliable delivery to the far end
        return Task.FromResult(true);
    }

    private void Receive(string fromUhid, byte[] data) => DataReceived?.Invoke(fromUhid, data);

    public Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public bool IsConnected(string peerUhid) => Peer is not null;
}

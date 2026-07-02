// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Heartbeat;

/// <summary>
/// Default heartbeat service. Broadcasts <see cref="PacketType.Heartbeat"/> beacons (TTL 1, one hop)
/// and tracks the liveness of peers from the heartbeats they broadcast. Unauthenticated by design —
/// like SOS, a heartbeat is a low-stakes liveness hint, not a security assertion.
/// </summary>
public sealed class HeartbeatService : IHeartbeatService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly ILogger<HeartbeatService> _logger;

    private int _sequence;
    private readonly ConcurrentDictionary<string, PeerLiveness> _peers = new(StringComparer.Ordinal);

    public event EventHandler<PeerLiveness>? PeerSeen;

    public HeartbeatService(IMeshSender sender, ILogger<HeartbeatService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<HeartbeatService>.Instance;
    }

    /// <inheritdoc />
    public async Task<int> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var payload = new HeartbeatPayload
        {
            Sequence = seq,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var packet = new MeshPacket
        {
            Type = PacketType.Heartbeat,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = 1, // heartbeats are single-hop: liveness of DIRECT neighbours only
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };

        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Heartbeat seq={Seq} broadcast to {Peers} peers", seq, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.Heartbeat)
            return Task.FromResult(false);

        // Ignore our own heartbeat echoed back.
        if (string.Equals(packet.SourceUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return Task.FromResult(false);

        HeartbeatPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<HeartbeatPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Heartbeat from {Source}: malformed payload — dropped", packet.SourceUhid);
            return Task.FromResult(false);
        }
        if (body is null) return Task.FromResult(false);

        var liveness = new PeerLiveness
        {
            Uhid = packet.SourceUhid,
            LastSequence = body.Sequence,
            LastSentAtMs = body.SentAtMs,
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _peers[packet.SourceUhid] = liveness;
        PeerSeen?.Invoke(this, liveness);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public IReadOnlyList<PeerLiveness> GetKnownPeers() => _peers.Values.ToArray();

    /// <inheritdoc />
    public IReadOnlyList<PeerLiveness> GetLivePeers(int withinSeconds)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)withinSeconds * 1000;
        return _peers.Values.Where(p => p.ReceivedAtMs >= cutoff).ToArray();
    }
}

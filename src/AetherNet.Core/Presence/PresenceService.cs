// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Presence;

/// <summary>
/// JSON payload for <see cref="PacketType.PresenceBeacon"/> (21) — a privacy-preserving "I'm here"
/// broadcast. It advertises the node's ROTATING <c>erid</c> (Ephemeral Routing Id, from EridDirectory —
/// never the stable UHID), a COARSE geohash (host-truncated; empty when hidden), its capability bitmask,
/// a presence status, and a send timestamp. Field order: erid, geohash, capabilities, status, sent_at_ms.
/// snake_case keys pinned by <see cref="JsonPropertyNameAttribute"/>. Byte-identity gate: fixtures/presence/vectors.json.
/// </summary>
public sealed class PresenceBeaconPayload
{
    /// <summary>The node's current rotating Ephemeral Routing Id (Crockford base-32). NOT the UHID.</summary>
    [JsonPropertyName("erid")] public string Erid { get; set; } = string.Empty;

    /// <summary>Coarse geohash of the node (host-truncated per privacy level); empty string = hidden.</summary>
    [JsonPropertyName("geohash")] public string Geohash { get; set; } = string.Empty;

    /// <summary>NodeCapabilities bitmask (BLE=1, WifiDirect=2, Gateway=4, Relay=8, …).</summary>
    [JsonPropertyName("capabilities")] public int Capabilities { get; set; }

    /// <summary>PresenceStatus value (Unknown=0, Available=1, Busy=2, Away=3, DoNotDisturb=4, Offline=5).</summary>
    [JsonPropertyName("status")] public int Status { get; set; }

    /// <summary>Unix timestamp (ms) when the beacon was sent.</summary>
    [JsonPropertyName("sent_at_ms")] public long SentAtMs { get; set; }
}

/// <summary>
/// JSON payload for <see cref="PacketType.PresenceQuery"/> (22) — "who's around here?". Broadcast to
/// solicit <see cref="PresenceBeaconPayload"/> replies. Field order: query_id, geohash. An empty geohash
/// means "anywhere". Byte-identity gate: fixtures/presence/vectors.json.
/// </summary>
public sealed class PresenceQueryPayload
{
    [JsonPropertyName("query_id")] public Guid QueryId { get; set; }
    [JsonPropertyName("geohash")] public string Geohash { get; set; } = string.Empty;
}

/// <summary>Event args: an inbound presence beacon plus the peer that sent it.</summary>
public sealed class PresenceBeaconReceived : EventArgs
{
    public PresenceBeaconPayload Beacon { get; init; } = new();
    public string FromUhid { get; init; } = string.Empty;
}

/// <summary>Event args: an inbound presence query plus the peer that sent it.</summary>
public sealed class PresenceQueryReceived : EventArgs
{
    public PresenceQueryPayload Query { get; init; } = new();
    public string FromUhid { get; init; } = string.Empty;
}

/// <summary>
/// Presence over <see cref="PacketType.PresenceBeacon"/> (21) and <see cref="PacketType.PresenceQuery"/>
/// (22). Broadcast a beacon (host builds it with the rotating erid + coarse geohash), broadcast a query,
/// and surface inbound beacons/queries via events. Transport only — the ERID rotation + geohash
/// coarsening are the host's concern (this service never touches the stable UHID or precise location).
/// </summary>
public interface IPresenceService
{
    event EventHandler<PresenceBeaconReceived>? BeaconReceived;
    event EventHandler<PresenceQueryReceived>? QueryReceived;

    /// <summary>Broadcast a presence beacon. Returns the number of peers reached.</summary>
    Task<int> BroadcastBeaconAsync(PresenceBeaconPayload beacon, CancellationToken cancellationToken = default);

    /// <summary>Broadcast a presence query for the given (coarse, possibly empty) geohash. Returns the new query id.</summary>
    Task<Guid> QueryAsync(string geohash, CancellationToken cancellationToken = default);

    /// <summary>Process an inbound presence packet (beacon or query). False on wrong type or malformed payload.</summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class PresenceService : IPresenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IMeshSender _sender;
    private readonly ILogger<PresenceService> _logger;

    public event EventHandler<PresenceBeaconReceived>? BeaconReceived;
    public event EventHandler<PresenceQueryReceived>? QueryReceived;

    public PresenceService(IMeshSender sender, ILogger<PresenceService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<PresenceService>.Instance;
    }

    /// <inheritdoc />
    public async Task<int> BroadcastBeaconAsync(PresenceBeaconPayload beacon, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beacon);
        var packet = new MeshPacket
        {
            Type = PacketType.PresenceBeacon,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(beacon, JsonOptions),
        };
        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Presence beacon (erid={Erid}) broadcast to {N} peers", beacon.Erid, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public async Task<Guid> QueryAsync(string geohash, CancellationToken cancellationToken = default)
    {
        var queryId = Guid.NewGuid();
        var payload = new PresenceQueryPayload { QueryId = queryId, Geohash = geohash ?? string.Empty };
        var packet = new MeshPacket
        {
            Type = PacketType.PresenceQuery,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        return queryId;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        try
        {
            switch (packet.Type)
            {
                case PacketType.PresenceBeacon:
                    var beacon = JsonSerializer.Deserialize<PresenceBeaconPayload>(packet.Payload, JsonOptions);
                    if (beacon is null || string.IsNullOrEmpty(beacon.Erid))
                        return Task.FromResult(false);
                    BeaconReceived?.Invoke(this, new PresenceBeaconReceived { Beacon = beacon, FromUhid = packet.SourceUhid });
                    return Task.FromResult(true);

                case PacketType.PresenceQuery:
                    var query = JsonSerializer.Deserialize<PresenceQueryPayload>(packet.Payload, JsonOptions);
                    if (query is null)
                        return Task.FromResult(false);
                    QueryReceived?.Invoke(this, new PresenceQueryReceived { Query = query, FromUhid = packet.SourceUhid });
                    return Task.FromResult(true);

                default:
                    return Task.FromResult(false);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Presence {Type} from {Source}: malformed payload — dropped", packet.Type, packet.SourceUhid);
            return Task.FromResult(false);
        }
    }
}

// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Diagnostics;
using AetherNet.Extensibility;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sos;

/// <summary>
/// Default SOS service. Originates SOS broadcasts as flooded
/// <see cref="PacketType.SosBroadcast"/> packets, optionally mirrored via
/// <see cref="IAetherNetBackendClient.SyncSosAsync"/>, and re-floods incoming alerts.
/// Dedups by packet id; rate-limited to <see cref="ProtocolConstants.MaxSosBroadcastsPerHour"/> per rolling hour.
/// </summary>
public sealed class SosBroadcastService : ISosBroadcastService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IAetherNetBackendClient _backend;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly ILogger<SosBroadcastService> _logger;

    private readonly ConcurrentQueue<DateTime> _recentOriginations = new();
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();
    private readonly ConcurrentDictionary<Guid, SosAlert> _activeAlerts = new();

    public event EventHandler<SosAlert>? SosReceived;
    public event EventHandler<Guid>? SosResolved;
    public event EventHandler<SosAcknowledgement>? SosAcknowledged;

    public SosBroadcastService(
        IMeshSender sender,
        IAetherNetBackendClient? backend = null,
        IAetherNetIncentiveProvider? incentives = null,
        ILogger<SosBroadcastService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _backend = backend ?? new DefaultBackendClient();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<SosBroadcastService>.Instance;
    }

    public async Task<bool> BroadcastSosAsync(
        string broadcastType,
        string? message,
        double latitude,
        double longitude,
        string? geohash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(broadcastType);

        PruneOldOriginations();
        if (_recentOriginations.Count >= ProtocolConstants.MaxSosBroadcastsPerHour)
        {
            _logger.LogWarning("SOS rate limited — {Count}/{Max} originations in the last hour",
                _recentOriginations.Count, ProtocolConstants.MaxSosBroadcastsPerHour);
            return false;
        }

        _recentOriginations.Enqueue(DateTime.UtcNow);

        var alert = new SosAlert
        {
            SenderUhid = _sender.LocalUhid,
            BroadcastType = broadcastType,
            Message = message,
            Latitude = latitude,
            Longitude = longitude,
            Geohash = geohash,
        };
        _activeAlerts[alert.Id] = alert;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new SosPayload
        {
            BroadcastId = alert.Id,
            BroadcastType = broadcastType,
            Message = message,
            Latitude = latitude,
            Longitude = longitude,
            Geohash = geohash,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.SosBroadcast,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.SosTtl,
            Priority = ProtocolConstants.SosPriority,
            Payload = payload,
        };
        _seen.TryAdd(packet.Id, 0);

        var meshTask = _sender.BroadcastAsync(packet, cancellationToken);
        var backendTask = _backend.SyncSosAsync(alert, cancellationToken);

        await Task.WhenAll(meshTask, backendTask).ConfigureAwait(false);

        AetherNetTelemetry.SosBroadcasts.Add(1);
        _logger.LogWarning("SOS originated {Id} type={Type} reach: mesh={Peers} backend={Backend}",
            alert.Id, broadcastType, meshTask.Result, backendTask.Result);

        return true;
    }

    public Task ResolveAsync(Guid broadcastId, CancellationToken cancellationToken = default)
    {
        if (_activeAlerts.TryRemove(broadcastId, out _))
        {
            SosResolved?.Invoke(this, broadcastId);
            _logger.LogInformation("SOS resolved locally: {Id}", broadcastId);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<SosAlert> GetActiveAlerts()
        => _activeAlerts.Values.ToArray();

    public async Task HandleAsync(MeshPacket sosPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sosPacket);
        if (sosPacket.Type != PacketType.SosBroadcast)
            throw new ArgumentException($"Expected SosBroadcast, got {sosPacket.Type}", nameof(sosPacket));

        if (!_seen.TryAdd(sosPacket.Id, 0))
        {
            AetherNetTelemetry.SosRebroadcastsSuppressed.Add(1);
            return;
        }

        SosPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<SosPayload>(sosPacket.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SOS: failed to deserialize payload from packet {Id}", sosPacket.Id);
            return;
        }
        if (body is null) return;

        if (string.Equals(sosPacket.SourceUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return;

        var alert = new SosAlert
        {
            Id = body.BroadcastId,
            SenderUhid = sosPacket.SourceUhid,
            BroadcastType = body.BroadcastType,
            Message = body.Message,
            Latitude = body.Latitude,
            Longitude = body.Longitude,
            Geohash = body.Geohash,
            ReceivedAt = DateTime.UtcNow,
        };
        _activeAlerts[alert.Id] = alert;
        SosReceived?.Invoke(this, alert);
        _logger.LogWarning("SOS received from {Source} id={Id} type={Type}",
            sosPacket.SourceUhid, alert.Id, alert.BroadcastType);

        // Acknowledge back to the originator so the sender learns their SOS reached a device.
        await SendSosAckAsync(alert.Id, sosPacket.SourceUhid, cancellationToken).ConfigureAwait(false);

        if (sosPacket.Ttl > 1)
        {
            sosPacket.Ttl--;
            var fanout = await _sender.BroadcastAsync(sosPacket, cancellationToken).ConfigureAwait(false);
            await _incentives.RecordRelayAsync(_sender.LocalUhid, sosPacket, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("SOS re-flooded id={Id} ttl={Ttl} fanout={Fanout}",
                alert.Id, sosPacket.Ttl, fanout);
        }
    }

    public Task HandleAckAsync(MeshPacket ackPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ackPacket);
        if (ackPacket.Type != PacketType.SosAck)
            throw new ArgumentException($"Expected SosAck, got {ackPacket.Type}", nameof(ackPacket));

        SosAckPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<SosAckPayload>(ackPacket.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SOS ack: failed to deserialize payload from packet {Id}", ackPacket.Id);
            return Task.CompletedTask;
        }
        if (body is null) return Task.CompletedTask;

        // Only the ORIGINATOR holds this alert in _activeAlerts; every other node ignores the ack.
        if (!_activeAlerts.TryGetValue(body.BroadcastId, out var alert))
            return Task.CompletedTask;

        var responder = ackPacket.SourceUhid;
        if (string.IsNullOrEmpty(responder)) return Task.CompletedTask;
        if (string.Equals(responder, _sender.LocalUhid, StringComparison.Ordinal))
            return Task.CompletedTask; // our own ack echoed back — ignore

        int total;
        lock (alert.AcknowledgedBy)
        {
            if (!alert.AcknowledgedBy.Add(responder))
                return Task.CompletedTask; // already counted this responder — dedup
            total = alert.AcknowledgedBy.Count;
        }

        SosAcknowledged?.Invoke(this, new SosAcknowledgement
        {
            BroadcastId = body.BroadcastId,
            ResponderUhid = responder,
            TotalAcknowledgements = total,
        });
        _logger.LogInformation("SOS {Id} acknowledged by {Responder} (distinct reach: {Total})",
            body.BroadcastId, responder, total);
        return Task.CompletedTask;
    }

    // Send a directed SosAck back to the alert originator so the sender learns their emergency
    // reached this device. Best-effort: delivers when the originator is reachable as a next hop.
    private async Task SendSosAckAsync(Guid broadcastId, string originatorUhid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(originatorUhid)) return;
        if (string.Equals(originatorUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new SosAckPayload
        {
            BroadcastId = broadcastId,
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, JsonOptions);

        var ack = new MeshPacket
        {
            Type = PacketType.SosAck,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = originatorUhid,
            Ttl = ProtocolConstants.SosTtl,
            Priority = ProtocolConstants.SosPriority,
            Payload = payload,
        };

        var delivered = await _sender.SendAsync(ack, originatorUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("SOS ack for {Id} → {Origin} delivered={Delivered}", broadcastId, originatorUhid, delivered);
    }

    private void PruneOldOriginations()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        while (_recentOriginations.TryPeek(out var oldest) && oldest < cutoff)
            _recentOriginations.TryDequeue(out _);
    }

    private sealed class SosPayload
    {
        public Guid BroadcastId { get; set; }
        public string BroadcastType { get; set; } = "sos";
        public string? Message { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? Geohash { get; set; }
    }

    private sealed class DefaultIncentiveProvider : IAetherNetIncentiveProvider
    {
    }

    private sealed class DefaultBackendClient : IAetherNetBackendClient
    {
    }
}

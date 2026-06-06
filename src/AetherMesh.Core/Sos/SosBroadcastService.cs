// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherMesh.Constants;
using AetherMesh.Diagnostics;
using AetherMesh.Extensibility;
using AetherMesh.Models;
using AetherMesh.Protocol;
using AetherMesh.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherMesh.Sos;

/// <summary>
/// Default SOS service. Originates SOS broadcasts as flooded
/// <see cref="PacketType.SosBroadcast"/> packets, optionally mirrored via
/// <see cref="IAetherMeshBackendClient.SyncSosAsync"/>, and re-floods incoming alerts.
/// Dedups by packet id; rate-limited to <see cref="ProtocolConstants.MaxSosBroadcastsPerHour"/> per rolling hour.
/// </summary>
public sealed class SosBroadcastService : ISosBroadcastService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IAetherMeshBackendClient _backend;
    private readonly IAetherMeshIncentiveProvider _incentives;
    private readonly ILogger<SosBroadcastService> _logger;

    private readonly ConcurrentQueue<DateTime> _recentOriginations = new();
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();
    private readonly ConcurrentDictionary<Guid, SosAlert> _activeAlerts = new();

    public event EventHandler<SosAlert>? SosReceived;
    public event EventHandler<Guid>? SosResolved;

    public SosBroadcastService(
        IMeshSender sender,
        IAetherMeshBackendClient? backend = null,
        IAetherMeshIncentiveProvider? incentives = null,
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

        AetherMeshTelemetry.SosBroadcasts.Add(1);
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
            AetherMeshTelemetry.SosRebroadcastsSuppressed.Add(1);
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

        if (sosPacket.Ttl > 1)
        {
            sosPacket.Ttl--;
            var fanout = await _sender.BroadcastAsync(sosPacket, cancellationToken).ConfigureAwait(false);
            await _incentives.RecordRelayAsync(_sender.LocalUhid, sosPacket, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("SOS re-flooded id={Id} ttl={Ttl} fanout={Fanout}",
                alert.Id, sosPacket.Ttl, fanout);
        }
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

    private sealed class DefaultIncentiveProvider : IAetherMeshIncentiveProvider
    {
    }

    private sealed class DefaultBackendClient : IAetherMeshBackendClient
    {
    }
}

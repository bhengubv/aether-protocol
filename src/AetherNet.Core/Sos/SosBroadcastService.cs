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
public sealed class SosBroadcastService : ISosBroadcastService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IAetherNetBackendClient _backend;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly TimeProvider _time;
    private readonly ILogger<SosBroadcastService> _logger;

    private readonly ConcurrentQueue<DateTime> _recentOriginations = new();
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();
    private readonly ConcurrentDictionary<Guid, SosAlert> _activeAlerts = new();
    private readonly ConcurrentDictionary<Guid, Origination> _originations = new();

    public event EventHandler<SosAlert>? SosReceived;
    public event EventHandler<Guid>? SosResolved;
    public event EventHandler<SosAcknowledgement>? SosAcknowledged;

    public SosBroadcastService(
        IMeshSender sender,
        IAetherNetBackendClient? backend = null,
        IAetherNetIncentiveProvider? incentives = null,
        TimeProvider? timeProvider = null,
        ILogger<SosBroadcastService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _backend = backend ?? new DefaultBackendClient();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _time = timeProvider ?? TimeProvider.System;
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

    /// <summary>
    /// Originate an SOS with an explicit reach, an optional contact list, and per-SOS check-in and beacon
    /// timings. Contacts get a directed send; Nearby floods; Both do both. A Contacts-only alert is a
    /// check-in / dead-man's switch: if the source does not mark itself safe within <paramref name="escalateAfter"/>
    /// it auto-widens to a full broadcast. Once broadcasting it re-emits a locator beacon every
    /// <paramref name="beaconInterval"/> — so rescuers keep receiving it and can triangulate — until the
    /// source marks safe, which is the ONLY thing that stops it (an acknowledgement never does).
    /// </summary>
    public async Task<bool> BroadcastSosAsync(
        string broadcastType,
        string? message,
        double latitude,
        double longitude,
        SosReach reach,
        IReadOnlyCollection<string>? contacts = null,
        TimeSpan? escalateAfter = null,
        TimeSpan? beaconInterval = null,
        string? geohash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(broadcastType);
        if (!TryReserveOrigination())
            return false;

        var escalate = escalateAfter ?? TimeSpan.FromSeconds(ProtocolConstants.SosDefaultEscalateAfterSeconds);
        var beacon = beaconInterval ?? TimeSpan.FromSeconds(ProtocolConstants.SosDefaultBeaconIntervalSeconds);
        var contactList = contacts?
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        var alert = new SosAlert
        {
            SenderUhid = _sender.LocalUhid,
            BroadcastType = broadcastType,
            Message = message,
            Latitude = latitude,
            Longitude = longitude,
            Geohash = geohash,
            Reach = reach,
        };
        _activeAlerts[alert.Id] = alert;

        var body = new SosPayload
        {
            BroadcastId = alert.Id,
            BroadcastType = broadcastType,
            Message = message,
            Latitude = latitude,
            Longitude = longitude,
            Geohash = geohash,
        };

        var origination = new Origination(alert, body, contactList, escalate, beacon);
        _originations[alert.Id] = origination;

        // Initial emission per the chosen reach.
        var reached = await EmitAsync(origination, cancellationToken).ConfigureAwait(false);
        await _backend.SyncSosAsync(alert, cancellationToken).ConfigureAwait(false);

        // Background lifecycle: check-in countdown -> escalate -> locator beacon. Cancelled ONLY by
        // ResolveAsync (source marks safe). Fire-and-forget; the loop owns its own cancellation + errors.
        _ = RunLifecycleAsync(origination);

        AetherNetTelemetry.SosBroadcasts.Add(1);
        _logger.LogWarning(
            "SOS originated {Id} type={Type} reach={Reach} escalateAfter={Escalate}s beacon={Beacon}s reached={Reached}",
            alert.Id, broadcastType, reach, escalate.TotalSeconds, beacon.TotalSeconds, reached);
        return true;
    }

    // Emit one round of the SOS according to its current reach. A fresh packet id per emission keeps a
    // locator beacon propagating (receivers dedupe by packet id) instead of being suppressed as a repeat.
    private async Task<int> EmitAsync(Origination o, CancellationToken cancellationToken)
    {
        var reached = 0;
        var reach = o.Alert.Reach;

        if (reach is SosReach.Nearby or SosReach.Both)
        {
            var flood = NewSosPacket(o, string.Empty);
            _seen.TryAdd(flood.Id, 0);
            reached += await _sender.BroadcastAsync(flood, cancellationToken).ConfigureAwait(false);
        }

        if (reach is SosReach.Contacts or SosReach.Both)
        {
            foreach (var contact in o.Contacts)
            {
                var directed = NewSosPacket(o, contact);
                _seen.TryAdd(directed.Id, 0);
                if (await _sender.SendAsync(directed, contact, cancellationToken).ConfigureAwait(false))
                    reached++;
            }
        }

        return reached;
    }

    private MeshPacket NewSosPacket(Origination o, string destinationUhid) => new()
    {
        Type = PacketType.SosBroadcast,
        SourceUhid = _sender.LocalUhid,
        DestinationUhid = destinationUhid,
        Ttl = ProtocolConstants.SosTtl,
        Priority = ProtocolConstants.SosPriority,
        Payload = JsonSerializer.SerializeToUtf8Bytes(o.Body, JsonOptions),
    };

    // The check-in -> escalate -> beacon lifecycle. The decisions it encodes are unit-tested in the pure
    // SosEscalationPolicy; here they are driven off the injected TimeProvider so tests advance a fake clock.
    private async Task RunLifecycleAsync(Origination o)
    {
        var ct = o.Cts.Token;
        try
        {
            // Phase 1 — check-in countdown (contacts-only). Marking safe cancels ct before this elapses;
            // if it elapses, no help came in time, so auto-widen to a full broadcast.
            if (o.Alert.Reach == SosReach.Contacts)
            {
                await Task.Delay(o.EscalateAfter, _time, ct).ConfigureAwait(false);
                o.Alert.Reach = SosReach.Both;
                o.Alert.Escalated = true;
                await EmitAsync(o, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "SOS {Id} escalated to broadcast — source not marked safe within {Window}s",
                    o.Alert.Id, o.EscalateAfter.TotalSeconds);
            }

            // Phase 2 — locator beacon: keep re-emitting while broadcasting, until the source marks safe.
            while (!ct.IsCancellationRequested
                   && SosEscalationPolicy.IsBroadcasting(o.Alert.Reach, o.Alert.Escalated))
            {
                await Task.Delay(o.BeaconInterval, _time, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    break;
                await EmitAsync(o, ct).ConfigureAwait(false);
                AetherNetTelemetry.SosBroadcasts.Add(1);
                _logger.LogDebug("SOS {Id} locator beacon re-emitted", o.Alert.Id);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled == source marked safe (ResolveAsync). Normal, expected termination.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SOS {Id} lifecycle loop faulted", o.Alert.Id);
        }
    }

    private bool TryReserveOrigination()
    {
        PruneOldOriginations();
        if (_recentOriginations.Count >= ProtocolConstants.MaxSosBroadcastsPerHour)
        {
            _logger.LogWarning("SOS rate limited — {Count}/{Max} originations in the last hour",
                _recentOriginations.Count, ProtocolConstants.MaxSosBroadcastsPerHour);
            return false;
        }
        _recentOriginations.Enqueue(DateTime.UtcNow);
        return true;
    }

    public Task ResolveAsync(Guid broadcastId, CancellationToken cancellationToken = default)
    {
        // Marking safe is the ONLY thing that stops an active SOS — cancel its check-in / beacon lifecycle.
        if (_originations.TryRemove(broadcastId, out var origination))
        {
            origination.Cts.Cancel();
            origination.Cts.Dispose();
        }

        if (_activeAlerts.TryRemove(broadcastId, out _))
        {
            SosResolved?.Invoke(this, broadcastId);
            _logger.LogInformation("SOS resolved locally (source marked safe): {Id}", broadcastId);
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

    /// <summary>Stops and clears every active SOS lifecycle (used on host shutdown).</summary>
    public void Dispose()
    {
        foreach (var o in _originations.Values)
        {
            try { o.Cts.Cancel(); o.Cts.Dispose(); }
            catch { /* best-effort teardown */ }
        }
        _originations.Clear();
    }

    // Per-origination lifecycle state: the alert, the payload body to re-emit, the contacts to reach,
    // the check-in and beacon timings, and the cancellation source that "mark safe" trips.
    private sealed class Origination
    {
        public Origination(SosAlert alert, SosPayload body, IReadOnlyList<string> contacts,
            TimeSpan escalateAfter, TimeSpan beaconInterval)
        {
            Alert = alert;
            Body = body;
            Contacts = contacts;
            EscalateAfter = escalateAfter;
            BeaconInterval = beaconInterval;
        }

        public SosAlert Alert { get; }
        public SosPayload Body { get; }
        public IReadOnlyList<string> Contacts { get; }
        public TimeSpan EscalateAfter { get; }
        public TimeSpan BeaconInterval { get; }
        public CancellationTokenSource Cts { get; } = new();
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

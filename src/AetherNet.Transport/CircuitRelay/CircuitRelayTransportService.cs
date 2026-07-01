// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.CircuitRelay;
using AetherNet.Models;
using AetherNet.Transport.Abstractions;

namespace AetherNet.Transport.CircuitRelay;

/// <summary>
/// Native circuit-relay-v2 transport. Any AetherNet node can act as a relay: a node that
/// cannot reach a peer directly routes through a third node that can reach both. This is
/// the decentralised, no-libp2p equivalent of libp2p's circuit-relay-v2 — a real
/// <see cref="ITransportService"/> that slots into the mesh next to BLE / Wi-Fi Direct /
/// WebRTC / the HTTP relay, not an app-level libp2p sidecar.
///
/// <para>Three roles, all in this one service (a node can be any/all at once):</para>
/// <list type="bullet">
///   <item><b>Target</b> — reserves capacity on a relay (<see cref="ReserveAsync"/>) so peers
///         behind NAT can be reached via that relay.</item>
///   <item><b>Client</b> — <see cref="SendAsync"/> to a peer for which a relay route is known
///         (<see cref="SetRoute"/>) performs the CONNECT handshake then tunnels DATA.</item>
///   <item><b>Relay</b> — grants reservations, bridges CONNECT→STOP, and forwards DATA
///         between the two legs under a data/duration budget.</item>
/// </list>
///
/// Frames are the native <see cref="RelayFrame"/> wire format (fixture-locked across all 8
/// languages). One hop of a frame is carried by the injected <see cref="IRelayLink"/>.
/// </summary>
public sealed class CircuitRelayTransportService : ITransportService, IDisposable
{
    private readonly string _localUhid;
    private readonly IRelayLink _link;
    private readonly CircuitRelayOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;

    // Relay role
    private readonly ConcurrentDictionary<string, DateTimeOffset> _reservations = new();
    private readonly ConcurrentDictionary<Guid, RelayBridge> _bridges = new();

    // Client / target role
    private readonly ConcurrentDictionary<string, string> _routes = new();               // dest -> relay
    private readonly ConcurrentDictionary<string, ActiveBridge> _peerBridges = new();     // peer -> bridge
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RelayStatus>> _pendingConnects = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayStatus>> _pendingReservations = new();

    private volatile bool _disposed;

    /// <param name="localUhid">This node's UHID.</param>
    /// <param name="link">One-hop link to directly-reachable nodes.</param>
    /// <param name="options">Policy/tuning (optional).</param>
    /// <param name="now">Clock (optional; injectable for deterministic reservation-expiry tests).</param>
    /// <param name="log">Optional line logger.</param>
    public CircuitRelayTransportService(
        string localUhid,
        IRelayLink link,
        CircuitRelayOptions? options = null,
        Func<DateTimeOffset>? now = null,
        Action<string>? log = null)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _options = options ?? new CircuitRelayOptions();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _link.FrameReceived += OnFrame;
    }

    // ── ITransportService ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "Circuit Relay (v2)";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 5_000_000; // relayed path; conservatively below a direct link

    /// <inheritdoc />
    public int MaxRangeMeters => 0; // internet-scope

    /// <summary>Relayed traffic is costly (an extra hop through a third node), so it sits just
    /// below the HTTP relay's last-resort cost of 100.</summary>
    public int PowerCostRelative => 90;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 256;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    /// <summary>True once a relay bridge to <paramref name="peerUhid"/> has been established.</summary>
    public bool IsConnected(string peerUhid) => _peerBridges.ContainsKey(peerUhid);

    /// <inheritdoc />
    public async Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_peerBridges.TryGetValue(peerUhid, out var existing))
            return await SendDataAsync(existing, peerUhid, data, cancellationToken).ConfigureAwait(false);

        // No bridge yet — establish one through the known relay for this peer.
        if (!_routes.TryGetValue(peerUhid, out var relay) || !_link.CanReach(relay))
        {
            _log?.Invoke($"[relay] no reachable relay route to {peerUhid}");
            return false;
        }

        var status = await ConnectAsync(peerUhid, relay, cancellationToken).ConfigureAwait(false);
        if (status != RelayStatus.Ok)
        {
            _log?.Invoke($"[relay] connect to {peerUhid} via {relay} failed: {status}");
            return false;
        }

        return _peerBridges.TryGetValue(peerUhid, out var b)
            && await SendDataAsync(b, peerUhid, data, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    // ── Public relay/target API ───────────────────────────────────────────────

    /// <summary>
    /// Reserves capacity on <paramref name="relayUhid"/> so peers can reach this node through
    /// it. Returns true once the relay confirms the reservation.
    /// </summary>
    public async Task<bool> ReserveAsync(string relayUhid, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_link.CanReach(relayUhid)) return false;

        var tcs = new TaskCompletionSource<RelayStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReservations[relayUhid] = tcs;
        try
        {
            var frame = new RelayFrame { Type = RelayMessageType.Reserve, SourceUhid = _localUhid, RelayUhid = relayUhid };
            await _link.SendFrameAsync(relayUhid, RelayFrameSerializer.Serialize(frame), cancellationToken).ConfigureAwait(false);
            var status = await AwaitStatus(tcs, _options.ReserveTimeout, cancellationToken).ConfigureAwait(false);
            return status == RelayStatus.Ok;
        }
        finally
        {
            _pendingReservations.TryRemove(relayUhid, out _);
        }
    }

    /// <summary>
    /// Records that <paramref name="destUhid"/> is reachable via relay <paramref name="relayUhid"/>.
    /// In production this is populated from the directory / reservation gossip; tests set it directly.
    /// </summary>
    public void SetRoute(string destUhid, string relayUhid) => _routes[destUhid] = relayUhid;

    /// <summary>Number of reservations this node is currently holding as a relay (diagnostics/tests).</summary>
    public int ActiveReservationCount => _reservations.Count;

    /// <summary>Number of bridges this node is currently servicing as a relay (diagnostics/tests).</summary>
    public int ActiveBridgeCount => _bridges.Count;

    // ── Client handshake ──────────────────────────────────────────────────────

    private async Task<RelayStatus> ConnectAsync(string dest, string relay, CancellationToken ct)
    {
        var connId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<RelayStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConnects[connId] = tcs;
        try
        {
            var frame = new RelayFrame
            {
                Type = RelayMessageType.Connect,
                SourceUhid = _localUhid,
                DestinationUhid = dest,
                RelayUhid = relay,
                ConnectionId = connId,
            };
            if (!await _link.SendFrameAsync(relay, RelayFrameSerializer.Serialize(frame), ct).ConfigureAwait(false))
                return RelayStatus.ConnectionFailed;

            return await AwaitStatus(tcs, _options.ConnectTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingConnects.TryRemove(connId, out _);
        }
    }

    private async Task<bool> SendDataAsync(ActiveBridge bridge, string peer, byte[] data, CancellationToken ct)
    {
        var frame = new RelayFrame
        {
            Type = RelayMessageType.Data,
            SourceUhid = _localUhid,
            DestinationUhid = peer,
            RelayUhid = bridge.RelayUhid,
            ConnectionId = bridge.ConnectionId,
            Payload = data,
        };
        return await _link.SendFrameAsync(bridge.RelayUhid, RelayFrameSerializer.Serialize(frame), ct).ConfigureAwait(false);
    }

    // ── Inbound frame dispatch ────────────────────────────────────────────────

    private void OnFrame(string fromNode, byte[] bytes)
    {
        if (_disposed) return;
        _ = DispatchAsync(fromNode, bytes);
    }

    private async Task DispatchAsync(string fromNode, byte[] bytes)
    {
        RelayFrame f;
        try { f = RelayFrameSerializer.Deserialize(bytes); }
        catch (Exception ex) { _log?.Invoke($"[relay] dropped malformed frame from {fromNode}: {ex.Message}"); return; }

        try
        {
            switch (f.Type)
            {
                case RelayMessageType.Reserve: await HandleReserveAsync(fromNode, f).ConfigureAwait(false); break;
                case RelayMessageType.ReserveResponse: HandleReserveResponse(fromNode, f); break;
                case RelayMessageType.Connect: await HandleConnectAsync(fromNode, f).ConfigureAwait(false); break;
                case RelayMessageType.Stop: await HandleStopAsync(fromNode, f).ConfigureAwait(false); break;
                case RelayMessageType.StopResponse: await HandleStopResponseAsync(fromNode, f).ConfigureAwait(false); break;
                case RelayMessageType.ConnectResponse: HandleConnectResponse(fromNode, f); break;
                case RelayMessageType.Data: await HandleDataAsync(fromNode, f).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[relay] handler error for {f.Type} from {fromNode}: {ex.Message}");
        }
    }

    // Relay: grant/refuse a reservation.
    private async Task HandleReserveAsync(string fromNode, RelayFrame f)
    {
        if (!_options.ActAsRelay || _reservations.Count >= _options.MaxReservations)
        {
            await SendReplyAsync(fromNode, RelayMessageType.ReserveResponse, f.SourceUhid, status: RelayStatus.ReservationRefused).ConfigureAwait(false);
            return;
        }

        var expiry = _now() + _options.ReservationTtl;
        _reservations[f.SourceUhid] = expiry;
        var reply = new RelayFrame
        {
            Type = RelayMessageType.ReserveResponse,
            SourceUhid = f.SourceUhid,
            RelayUhid = _localUhid,
            Status = RelayStatus.Ok,
            ReservationExpiresAtMs = expiry.ToUnixTimeMilliseconds(),
        };
        await _link.SendFrameAsync(fromNode, RelayFrameSerializer.Serialize(reply)).ConfigureAwait(false);
    }

    // Client: reservation confirmed/denied.
    private void HandleReserveResponse(string fromNode, RelayFrame f)
    {
        if (_pendingReservations.TryGetValue(fromNode, out var tcs))
            tcs.TrySetResult(f.Status);
    }

    // Relay: A wants to reach B. Validate B's reservation + reachability, then open a STOP to B.
    private async Task HandleConnectAsync(string fromNode, RelayFrame f)
    {
        var a = f.SourceUhid;
        var b = f.DestinationUhid;

        if (!_options.ActAsRelay)
        {
            await ReplyConnectAsync(a, f, RelayStatus.ConnectionFailed).ConfigureAwait(false); return;
        }
        if (!_reservations.TryGetValue(b, out var exp) || _now() >= exp)
        {
            _reservations.TryRemove(b, out _);
            await ReplyConnectAsync(a, f, RelayStatus.NoReservation).ConfigureAwait(false); return;
        }
        if (!_link.CanReach(b))
        {
            await ReplyConnectAsync(a, f, RelayStatus.ConnectionFailed).ConfigureAwait(false); return;
        }
        if (_bridges.Count >= _options.MaxBridges)
        {
            await ReplyConnectAsync(a, f, RelayStatus.ResourceLimitExceeded).ConfigureAwait(false); return;
        }

        var deadline = _options.BridgeDurationLimitSeconds > 0
            ? _now().AddSeconds(_options.BridgeDurationLimitSeconds)
            : DateTimeOffset.MaxValue;
        _bridges[f.ConnectionId] = new RelayBridge(a, b, _options.BridgeDataLimitBytes, deadline);

        var stop = new RelayFrame
        {
            Type = RelayMessageType.Stop,
            SourceUhid = a,
            DestinationUhid = b,
            RelayUhid = _localUhid,
            ConnectionId = f.ConnectionId,
            LimitDataBytes = _options.BridgeDataLimitBytes,
            LimitDurationSeconds = _options.BridgeDurationLimitSeconds,
        };
        await _link.SendFrameAsync(b, RelayFrameSerializer.Serialize(stop)).ConfigureAwait(false);
    }

    // Target: relay says A wants to reach us. Accept and record a return route to A.
    private async Task HandleStopAsync(string fromNode, RelayFrame f)
    {
        _peerBridges[f.SourceUhid] = new ActiveBridge(f.ConnectionId, fromNode);
        var reply = new RelayFrame
        {
            Type = RelayMessageType.StopResponse,
            SourceUhid = f.SourceUhid,
            DestinationUhid = _localUhid,
            RelayUhid = fromNode,
            ConnectionId = f.ConnectionId,
            Status = RelayStatus.Ok,
        };
        await _link.SendFrameAsync(fromNode, RelayFrameSerializer.Serialize(reply)).ConfigureAwait(false);
    }

    // Relay: target accepted/refused. Finalise the bridge and answer the client.
    private async Task HandleStopResponseAsync(string fromNode, RelayFrame f)
    {
        if (!_bridges.TryGetValue(f.ConnectionId, out var bridge)) return;

        if (f.Status != RelayStatus.Ok)
        {
            _bridges.TryRemove(f.ConnectionId, out _);
            await ReplyConnectAsync(bridge.AUhid, f, RelayStatus.ConnectionFailed).ConfigureAwait(false);
            return;
        }

        bridge.Open = true;
        var ok = new RelayFrame
        {
            Type = RelayMessageType.ConnectResponse,
            SourceUhid = bridge.AUhid,
            DestinationUhid = bridge.BUhid,
            RelayUhid = _localUhid,
            ConnectionId = f.ConnectionId,
            Status = RelayStatus.Ok,
            LimitDataBytes = bridge.DataBudget,
        };
        await _link.SendFrameAsync(bridge.AUhid, RelayFrameSerializer.Serialize(ok)).ConfigureAwait(false);
    }

    // Client: bridge established/refused.
    private void HandleConnectResponse(string fromNode, RelayFrame f)
    {
        if (f.Status == RelayStatus.Ok)
            _peerBridges[f.DestinationUhid] = new ActiveBridge(f.ConnectionId, fromNode);

        if (_pendingConnects.TryGetValue(f.ConnectionId, out var tcs))
            tcs.TrySetResult(f.Status);
    }

    // Data: either I'm an endpoint (deliver) or the relay (forward the other way, under budget).
    private async Task HandleDataAsync(string fromNode, RelayFrame f)
    {
        if (f.DestinationUhid == _localUhid)
        {
            DataReceived?.Invoke(f.SourceUhid, f.Payload);
            return;
        }

        if (!_bridges.TryGetValue(f.ConnectionId, out var bridge) || !bridge.Open)
            return; // unknown / not-yet-open bridge — drop

        if (fromNode != bridge.AUhid && fromNode != bridge.BUhid)
            return; // frame not from a party to this bridge

        if (bridge.Deadline <= _now())
        {
            _bridges.TryRemove(f.ConnectionId, out _);
            return;
        }

        var used = Interlocked.Add(ref bridge.DataUsed, f.Payload.Length);
        if (bridge.DataBudget > 0 && used > bridge.DataBudget)
        {
            _bridges.TryRemove(f.ConnectionId, out _);
            _log?.Invoke($"[relay] bridge {f.ConnectionId} exceeded data budget ({used}/{bridge.DataBudget})");
            return;
        }

        // Forward the frame unchanged to the other endpoint (= its dst).
        await _link.SendFrameAsync(f.DestinationUhid, RelayFrameSerializer.Serialize(f)).ConfigureAwait(false);
    }

    // ── Reply helpers ─────────────────────────────────────────────────────────

    private Task SendReplyAsync(string toNode, RelayMessageType type, string source, RelayStatus status)
    {
        var reply = new RelayFrame { Type = type, SourceUhid = source, RelayUhid = _localUhid, Status = status };
        return _link.SendFrameAsync(toNode, RelayFrameSerializer.Serialize(reply));
    }

    private Task ReplyConnectAsync(string clientUhid, RelayFrame connect, RelayStatus status)
    {
        var reply = new RelayFrame
        {
            Type = RelayMessageType.ConnectResponse,
            SourceUhid = connect.SourceUhid,
            DestinationUhid = connect.DestinationUhid,
            RelayUhid = _localUhid,
            ConnectionId = connect.ConnectionId,
            Status = status,
        };
        return _link.SendFrameAsync(clientUhid, RelayFrameSerializer.Serialize(reply));
    }

    private static async Task<RelayStatus> AwaitStatus(
        TaskCompletionSource<RelayStatus> tcs, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, cts.Token);
        var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
        if (done == tcs.Task)
        {
            cts.Cancel();
            return await tcs.Task.ConfigureAwait(false);
        }
        return RelayStatus.ConnectionFailed; // timeout
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _link.FrameReceived -= OnFrame;
        foreach (var tcs in _pendingConnects.Values) tcs.TrySetResult(RelayStatus.ConnectionFailed);
        foreach (var tcs in _pendingReservations.Values) tcs.TrySetResult(RelayStatus.ConnectionFailed);
    }

    // ── State records ─────────────────────────────────────────────────────────

    /// <summary>A bridge this node is relaying (mutable data counter for Interlocked).</summary>
    private sealed class RelayBridge(string aUhid, string bUhid, long dataBudget, DateTimeOffset deadline)
    {
        public string AUhid { get; } = aUhid;
        public string BUhid { get; } = bUhid;
        public long DataBudget { get; } = dataBudget;
        public DateTimeOffset Deadline { get; } = deadline;
        public long DataUsed;          // field (not property) for Interlocked.Add
        public volatile bool Open;
    }

    /// <summary>An established bridge from this node's endpoint view: which connection, via which relay.</summary>
    private readonly record struct ActiveBridge(Guid ConnectionId, string RelayUhid);
}

// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Aether.Constants;
using Aether.Diagnostics;
using Aether.Extensibility;
using Aether.Models;
using Aether.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.Routing;

/// <summary>
/// AODV-inspired reactive routing. RREQ floods the mesh; the destination (or any node
/// holding a fresh route to it) replies with an RREP that installs forward and reverse
/// routes hop-by-hop along the way.
///
/// Routes are cached in memory and persisted via <see cref="IRouteStore"/>. RREQ
/// duplicates are dropped using an in-memory packet-ID set. Pending route discoveries
/// register a <see cref="TaskCompletionSource{TResult}"/> that the matching RREP completes;
/// timeouts are bounded by <see cref="ProtocolConstants.RouteTimeoutMs"/>.
/// </summary>
public sealed class RoutingService : IRoutingService
{
    private readonly IMeshSender _sender;
    private readonly IRouteStore _store;
    private readonly IRouteReplyVerifier _verifier;
    private readonly IAetherIncentiveProvider _incentives;
    private readonly ILogger<RoutingService> _logger;

    private readonly ConcurrentDictionary<string, RouteEntry> _routeCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RouteEntry>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> _seenRreqs = new();

    private int _loaded;

    public RoutingService(
        IMeshSender sender,
        IRouteStore? store = null,
        IRouteReplyVerifier? verifier = null,
        IAetherIncentiveProvider? incentives = null,
        ILogger<RoutingService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _store = store ?? new InMemoryRouteStore();
        _verifier = verifier ?? new AcceptAllRouteReplyVerifier();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<RoutingService>.Instance;
    }

    public async Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationUhid);

        using var activity = AetherTelemetry.ActivitySource.StartActivity("Aether.Route.Lookup");
        if (activity is not null)
            activity.SetTag("aether.destination.uhid", AetherTelemetry.SanitizeUhid(destinationUhid));

        var stopwatch = ValueStopwatch.StartNew();
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);

            if (_routeCache.TryGetValue(destinationUhid, out var cached) && !cached.IsExpired)
            {
                AetherTelemetry.RouteCacheHits.Add(1);
                if (activity is not null)
                {
                    activity.SetTag("aether.route.source", "cache");
                    activity.SetTag("aether.route.hops", cached.HopCount);
                }
                return cached;
            }

            var stored = await _store.GetAsync(destinationUhid, cancellationToken).ConfigureAwait(false);
            if (stored is not null && !stored.IsExpired)
            {
                _routeCache[destinationUhid] = stored;
                AetherTelemetry.RouteCacheHits.Add(1);
                if (activity is not null)
                {
                    activity.SetTag("aether.route.source", "store");
                    activity.SetTag("aether.route.hops", stored.HopCount);
                }
                return stored;
            }

            var discovered = await DiscoverAsync(destinationUhid, cancellationToken).ConfigureAwait(false);
            if (activity is not null)
            {
                activity.SetTag("aether.route.source", "discovery");
                if (discovered is not null)
                    activity.SetTag("aether.route.hops", discovered.HopCount);
            }
            return discovered;
        }
        finally
        {
            AetherTelemetry.RouteLookupLatency.Record(stopwatch.GetElapsedMilliseconds());
        }
    }

    public RouteEntry? GetCachedRoute(string destinationUhid)
    {
        if (string.IsNullOrEmpty(destinationUhid)) return null;
        return _routeCache.TryGetValue(destinationUhid, out var route) && !route.IsExpired ? route : null;
    }

    public IReadOnlyList<RouteEntry> GetAllRoutes()
        => _routeCache.Values.Where(r => !r.IsExpired).ToArray();

    public async Task HandleRouteRequestAsync(MeshPacket routeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeRequest);
        if (routeRequest.Type != PacketType.RouteRequest)
            throw new ArgumentException($"Expected RouteRequest, got {routeRequest.Type}", nameof(routeRequest));

        if (!_seenRreqs.TryAdd(routeRequest.Id, 0))
            return;

        var localUhid = _sender.LocalUhid;
        if (string.IsNullOrEmpty(routeRequest.SourceUhid) || routeRequest.SourceUhid == localUhid)
            return;

        var hopCount = Math.Max(1, ProtocolConstants.DefaultTtl - routeRequest.Ttl + 1);
        var reverse = new RouteEntry
        {
            DestinationUhid = routeRequest.SourceUhid,
            NextHopUhid = routeRequest.SourceUhid,
            HopCount = hopCount,
            LatencyMs = 0,
            QualityScore = RouteEntry.ComputeQuality(hopCount, 0, 0.5),
            ExpiresAt = DateTime.UtcNow.AddSeconds(ProtocolConstants.RouteExpirySeconds),
        };
        _routeCache[reverse.DestinationUhid] = reverse;
        await _store.SaveAsync(reverse, cancellationToken).ConfigureAwait(false);

        if (routeRequest.DestinationUhid == localUhid)
        {
            await SendRouteReplyAsync(localUhid, routeRequest, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("RREP sent — local node is destination of RREQ from {Source}", routeRequest.SourceUhid);
            return;
        }

        if (_routeCache.TryGetValue(routeRequest.DestinationUhid, out var known) && !known.IsExpired)
        {
            await SendRouteReplyAsync(routeRequest.DestinationUhid, routeRequest, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("RREP sent on behalf of {Dest} for RREQ from {Source}",
                routeRequest.DestinationUhid, routeRequest.SourceUhid);
            return;
        }

        if (routeRequest.Ttl > 1)
        {
            routeRequest.Ttl--;
            var fanout = await _sender.BroadcastAsync(routeRequest, cancellationToken).ConfigureAwait(false);
            await _incentives.RecordRelayAsync(localUhid, routeRequest, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("RREQ forwarded for {Dest} to {Fanout} peers (ttl={Ttl})",
                routeRequest.DestinationUhid, fanout, routeRequest.Ttl);
        }
    }

    public async Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeReply);
        if (routeReply.Type != PacketType.RouteReply)
            throw new ArgumentException($"Expected RouteReply, got {routeReply.Type}", nameof(routeReply));

        if (!await _verifier.VerifyAsync(routeReply, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("RREP from {Source} rejected by verifier — dropped (possible spoofing)",
                routeReply.SourceUhid);
            return;
        }

        var localUhid = _sender.LocalUhid;
        if (string.IsNullOrEmpty(routeReply.SourceUhid) || routeReply.SourceUhid == localUhid)
            return;

        var hopCount = Math.Max(1, ProtocolConstants.DefaultTtl - routeReply.Ttl + 1);
        var forward = new RouteEntry
        {
            DestinationUhid = routeReply.SourceUhid,
            NextHopUhid = routeReply.SourceUhid,
            HopCount = hopCount,
            LatencyMs = 0,
            QualityScore = RouteEntry.ComputeQuality(hopCount, 0, 0.5),
            ExpiresAt = DateTime.UtcNow.AddSeconds(ProtocolConstants.RouteExpirySeconds),
        };
        _routeCache[forward.DestinationUhid] = forward;
        await _store.SaveAsync(forward, cancellationToken).ConfigureAwait(false);
        AetherTelemetry.RouteRepliesReceived.Add(1);
        _logger.LogDebug("Forward route installed to {Dest} via RREP", forward.DestinationUhid);

        if (routeReply.DestinationUhid == localUhid)
        {
            if (_pending.TryRemove(routeReply.SourceUhid, out var tcs))
                tcs.TrySetResult(forward);
            return;
        }

        if (routeReply.Ttl <= 1)
            return;

        if (_routeCache.TryGetValue(routeReply.DestinationUhid, out var nextHop) && !nextHop.IsExpired)
        {
            routeReply.Ttl--;
            var delivered = await _sender.SendAsync(routeReply, nextHop.NextHopUhid, cancellationToken).ConfigureAwait(false);
            if (delivered)
                await _incentives.RecordRelayAsync(localUhid, routeReply, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("RREP forwarded toward {Dest} via {Hop}: delivered={Delivered}",
                routeReply.DestinationUhid, nextHop.NextHopUhid, delivered);
        }
        else
        {
            _logger.LogDebug("RREP for {Dest} cannot be forwarded — no reverse route", routeReply.DestinationUhid);
        }
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        var pruned = 0;
        foreach (var kvp in _routeCache)
        {
            if (kvp.Value.IsExpired && _routeCache.TryRemove(kvp.Key, out _))
                pruned++;
        }
        var storePruned = await _store.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);

        if (_seenRreqs.Count > ProtocolConstants.DeduplicationCacheSize)
            _seenRreqs.Clear();

        if (pruned > 0 || storePruned > 0)
            _logger.LogDebug("Pruned {Memory} cached + {Store} stored expired routes", pruned, storePruned);
    }

    private async Task<RouteEntry?> DiscoverAsync(string destinationUhid, CancellationToken cancellationToken)
    {
        var existing = _pending.GetOrAdd(destinationUhid,
            _ => new TaskCompletionSource<RouteEntry>(TaskCreationOptions.RunContinuationsAsynchronously));

        var rreq = new MeshPacket
        {
            Type = PacketType.RouteRequest,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = destinationUhid,
            Ttl = ProtocolConstants.DefaultTtl,
        };

        AetherTelemetry.RouteRequestsEmitted.Add(1);
        var fanout = await _sender.BroadcastAsync(rreq, cancellationToken).ConfigureAwait(false);
        if (fanout == 0)
        {
            _pending.TryRemove(destinationUhid, out _);
            existing.TrySetResult(null!);
            _logger.LogDebug("RREQ for {Dest} — no peers connected, discovery aborted", destinationUhid);
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProtocolConstants.RouteTimeoutMs);
            await using var registration = timeout.Token.Register(() => existing.TrySetCanceled()).ConfigureAwait(false);
            return await existing.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("RREQ timeout — no RREP received for {Dest}", destinationUhid);
            return null;
        }
        finally
        {
            _pending.TryRemove(destinationUhid, out _);
        }
    }

    private async Task SendRouteReplyAsync(string repliedSource, MeshPacket originalRreq, CancellationToken cancellationToken)
    {
        var rrep = new MeshPacket
        {
            Type = PacketType.RouteReply,
            SourceUhid = repliedSource,
            DestinationUhid = originalRreq.SourceUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = originalRreq.Payload,
        };

        if (_routeCache.TryGetValue(originalRreq.SourceUhid, out var reverse) && !reverse.IsExpired)
            await _sender.SendAsync(rrep, reverse.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(rrep, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync()
    {
        if (Interlocked.CompareExchange(ref _loaded, 1, 0) != 0) return;

        try
        {
            var stored = await _store.GetAllAsync().ConfigureAwait(false);
            foreach (var route in stored)
                if (!route.IsExpired)
                    _routeCache.TryAdd(route.DestinationUhid, route);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load routes from store; starting with empty cache");
            Interlocked.Exchange(ref _loaded, 0);
        }
    }

    private sealed class DefaultIncentiveProvider : IAetherIncentiveProvider
    {
    }
}

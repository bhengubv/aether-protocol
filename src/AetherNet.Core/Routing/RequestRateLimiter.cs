// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Routing;

/// <summary>
/// One simple cap on discovery requests (RouteRequest / NameQuery), applied at the relay —
/// where a flood amplifies. A legitimate node finds a route or resolves a name a handful of
/// times and then caches the answer, so the natural rate is tiny; anything above the cap is a
/// flood and is dropped. The caller also scores the source down, so a persistent flooder is
/// excommunicated network-wide.
///
/// <para>Two token buckets per check:</para>
/// <list type="bullet">
///   <item><b>per-source</b> — one node can't flood (burst up to <c>capacity</c>, then refill at
///     <c>refillPerSec</c>). A short burst is allowed so a legit app-launch resolving several
///     names at once doesn't trip.</item>
///   <item><b>aggregate</b> — a sybil swarm, each node under its own per-source cap, still can't
///     collectively exceed the relay's ceiling. A known source is checked against its own bucket
///     first (so one flooder past its cap can't drain the shared ceiling on requests that would be
///     rejected anyway); a never-seen source is gated by the aggregate before any bucket is
///     allocated, so the limiter can't be turned into a memory-exhaustion vector itself.</item>
/// </list>
///
/// <para>Idle per-source buckets (refilled back to full) are pruned once the map grows large,
/// bounding memory no matter how many distinct source identities an attacker presents.</para>
/// </summary>
public sealed class RequestRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastMs;
    }

    private readonly int _capacity;
    private readonly double _refillPerSec;
    private readonly int _aggregateCapacity;
    private readonly double _aggregateRefillPerSec;
    private readonly int _maxSources;
    private readonly Func<long> _nowMs;

    private readonly ConcurrentDictionary<string, Bucket> _perSource = new(StringComparer.Ordinal);
    private readonly Bucket _aggregate;

    /// <summary>
    /// Defaults: a 10-request burst per source refilling at 10/min; a 200-request relay aggregate
    /// refilling at 200/min; at most 8192 tracked sources before idle ones are pruned.
    /// </summary>
    /// <param name="nowMs">Monotonic clock in ms — injectable so tests can advance time. Defaults
    /// to <see cref="Environment.TickCount64"/>.</param>
    public RequestRateLimiter(
        int capacity = 10,
        double refillPerSec = 10.0 / 60.0,
        int aggregateCapacity = 200,
        double aggregateRefillPerSec = 200.0 / 60.0,
        int maxSources = 8192,
        Func<long>? nowMs = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSec <= 0) throw new ArgumentOutOfRangeException(nameof(refillPerSec));
        if (aggregateCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(aggregateCapacity));
        if (aggregateRefillPerSec <= 0) throw new ArgumentOutOfRangeException(nameof(aggregateRefillPerSec));
        if (maxSources <= 0) throw new ArgumentOutOfRangeException(nameof(maxSources));

        _capacity = capacity;
        _refillPerSec = refillPerSec;
        _aggregateCapacity = aggregateCapacity;
        _aggregateRefillPerSec = aggregateRefillPerSec;
        _maxSources = maxSources;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _aggregate = new Bucket { Tokens = aggregateCapacity, LastMs = _nowMs() };
    }

    /// <summary>
    /// Returns true if a request from <paramref name="sourceUhid"/> is within budget (allow,
    /// consuming one token); false if it exceeds the aggregate OR the per-source rate (drop).
    /// </summary>
    public bool TryAcquire(string sourceUhid)
    {
        var key = sourceUhid ?? string.Empty;
        var now = _nowMs();

        // 1. KNOWN source → check its per-source bucket FIRST and reject an over-cap flooder here,
        //    WITHOUT spending an aggregate token. Otherwise a single flooder, already past its own
        //    cap, would keep draining the shared aggregate on requests that get per-source-rejected
        //    anyway — starving every other source. (Read-only peek; the authoritative consume is §3.)
        if (_perSource.TryGetValue(key, out var existing))
        {
            lock (existing)
            {
                Refill(existing, now, _capacity, _refillPerSec);
                if (existing.Tokens < 1.0) return false;
            }
        }

        // 2. Aggregate gate — checked before a NEW bucket is allocated, so a saturated relay drops
        //    a never-seen source without growing the map: the limiter can't be a memory-exhaustion
        //    vector itself. A known source has already passed §1, so it only reaches here within its
        //    own cap, bounding how much of the aggregate any one source can consume.
        lock (_aggregate)
        {
            Refill(_aggregate, now, _aggregateCapacity, _aggregateRefillPerSec);
            if (_aggregate.Tokens < 1.0) return false;
            _aggregate.Tokens -= 1.0;
        }

        // 3. Consume the per-source token (creating the bucket on first sighting).
        var bucket = _perSource.GetOrAdd(key, _ => new Bucket { Tokens = _capacity, LastMs = now });
        bool allowed;
        lock (bucket)
        {
            Refill(bucket, now, _capacity, _refillPerSec);
            allowed = bucket.Tokens >= 1.0;
            if (allowed) bucket.Tokens -= 1.0;
        }

        if (_perSource.Count > _maxSources) PruneIdle(now);
        return allowed;
    }

    /// <summary>Number of per-source buckets currently tracked — exposed for monitoring and tests.
    /// Bounded by the aggregate gate (no new bucket once the relay is saturated) plus the idle prune.</summary>
    public int TrackedSourceCount => _perSource.Count;

    private static void Refill(Bucket b, long now, int capacity, double refillPerSec)
    {
        var elapsedSec = (now - b.LastMs) / 1000.0;
        if (elapsedSec <= 0) return;
        b.Tokens = Math.Min(capacity, b.Tokens + elapsedSec * refillPerSec);
        b.LastMs = now;
    }

    // Drop buckets that have refilled to full: a full bucket is indistinguishable from a fresh one,
    // so removing it changes nothing except freeing memory. Bounds the map under sybil churn.
    private void PruneIdle(long now)
    {
        foreach (var kvp in _perSource)
        {
            var b = kvp.Value;
            lock (b)
            {
                Refill(b, now, _capacity, _refillPerSec);
                if (b.Tokens >= _capacity)
                    _perSource.TryRemove(kvp.Key, out _);
            }
        }
    }
}

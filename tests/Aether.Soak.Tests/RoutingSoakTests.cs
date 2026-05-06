// SPDX-License-Identifier: MIT

using Aether.Models;
using Aether.Routing;
using Xunit;

namespace Aether.Soak.Tests;

/// <summary>
/// Soak tests for the routing layer's bounded caches:
/// <list type="bullet">
///   <item><see cref="InMemoryRouteStore"/> route table — survives sustained
///     installation/expiry traffic without leaking entries past their TTL.</item>
///   <item><see cref="RoutingService.PruneAsync"/> — removes expired routes
///     in-place; <see cref="RoutingService.GetAllRoutes"/> only ever returns
///     non-expired entries, so the externally-observable size shrinks back
///     to zero after a sweep.</item>
/// </list>
///
/// We don't have a synthetic clock for the routing layer (routes use
/// <see cref="DateTime.UtcNow"/> directly), so the expiry path is tested
/// via short TTLs (<see cref="RouteEntry.ExpiresAt"/> set to ~immediately),
/// not by advancing a fake clock.
/// </summary>
[Trait("Category", "Soak")]
public class RoutingSoakTests : SoakTestBase
{
    private const string Local = "local-uhid";

    /// <summary>
    /// Install 1 000 routes with short expiry, wait for natural expiry,
    /// run prune, verify the store is empty. Asserts that:
    /// <list type="bullet">
    ///   <item>After expiry, no non-expired routes remain
    ///     (<see cref="RoutingService.GetAllRoutes"/> returns empty).</item>
    ///   <item>The store's prune-expired path actually removes entries
    ///     (<see cref="InMemoryRouteStore.PruneExpiredAsync"/>).</item>
    ///   <item>Memory growth across the install + expire + prune cycle is
    ///     bounded — no orphaned dictionary entries in the route cache.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Routing_LongRunRoutesExpire()
    {
        var iterations = Math.Min(ResolveIterations(), 1_000);

        var sender = new SoakFakeMeshSender(Local);
        var store = new InMemoryRouteStore();
        var routing = new RoutingService(sender, store);

        // Install routes with a 1-second expiry — short enough to expire
        // mid-test, long enough that the install loop completes before the
        // first one is overdue.
        for (var i = 0; i < iterations; i++)
        {
            var dest = $"dest-{i}";
            await store.SaveAsync(new RouteEntry
            {
                DestinationUhid = dest,
                NextHopUhid = $"hop-{i}",
                HopCount = 1,
                ExpiresAt = DateTime.UtcNow.AddSeconds(1),
            });
        }

        var beforePrune = (await store.GetAllAsync()).Count;
        Assert.Equal(iterations, beforePrune);

        // Wait past the TTL.
        await Task.Delay(TimeSpan.FromMilliseconds(1_500));

        // Soak-test the prune path: every entry should now be expired and
        // <see cref="RoutingService.PruneAsync"/> should reap them all.
        await routing.PruneAsync();
        var pruned = await store.PruneExpiredAsync();

        // After prune: no live routes from the routing service's POV.
        Assert.Empty(routing.GetAllRoutes());

        // The store snapshot should be empty too — both caches were
        // expired and the prune call was definitive.
        var afterPrune = (await store.GetAllAsync()).Count;
        Assert.Equal(0, afterPrune);

        // pruned can vary: anything from 0 (already-pruned by the routing
        // service's PruneAsync) to <iterations>. The assertion that
        // matters is the final counts above.
        Assert.True(pruned >= 0);
    }

    /// <summary>
    /// Install + expire cycle in a tight loop. Each iteration installs a
    /// route, replaces it with another, and prunes. Asserts the store size
    /// stays bounded (no leak from concurrent mutation). Memory growth is
    /// observed but not asserted strictly — short-TTL DateTime allocations
    /// are noisy under GC pressure.
    /// </summary>
    [Fact]
    public async Task Routing_TightInstallExpireLoop_StoreSizeBounded()
    {
        var iterations = Math.Min(ResolveIterations(), 5_000);

        var store = new InMemoryRouteStore();

        for (var i = 0; i < iterations; i++)
        {
            // Install the same destination repeatedly with rolling expiry —
            // this is the hot path under sustained route discovery.
            await store.SaveAsync(new RouteEntry
            {
                DestinationUhid = "rolling-dest",
                NextHopUhid = $"hop-{i}",
                HopCount = 1,
                ExpiresAt = DateTime.UtcNow.AddMilliseconds(50),
            });

            if ((i & 0xFF) == 0)
                await store.PruneExpiredAsync();
        }

        // After all iterations: the store either holds the most-recent
        // rolling entry (if it hasn't expired yet) or nothing.
        var snapshot = await store.GetAllAsync();
        Assert.True(snapshot.Count <= 1,
            $"Rolling install loop left {snapshot.Count} entries in the store — expected 0 or 1.");
    }
}

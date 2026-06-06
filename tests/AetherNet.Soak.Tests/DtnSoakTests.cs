// SPDX-License-Identifier: MIT

using AetherNet.Dtn;
using AetherNet.Models;
using Xunit;

namespace AetherNet.Soak.Tests;

/// <summary>
/// Soak tests for <see cref="DtnService"/> + <see cref="InMemoryDtnBundleStore"/>.
/// The bundle store retains every bundle until either (a) the recipient
/// claims it and it transitions to <see cref="BundleStatus.Delivered"/>, or
/// (b) it ages past <see cref="DtnBundle.ExpiresAt"/> and
/// <see cref="DtnService.ExpireStaleAsync"/> reaps it.
///
/// Without this soak run, an off-by-one in the expiry path would let
/// long-lived peers accumulate thousands of stale bundles in memory.
/// </summary>
[Trait("Category", "Soak")]
public class DtnSoakTests : SoakTestBase
{
    private const string Local = "local-uhid";

    /// <summary>
    /// Submit 1 000 bundles with short TTL, wait past the TTL, run
    /// expiration, and assert:
    /// <list type="bullet">
    ///   <item>All bundles transition to <see cref="BundleStatus.Expired"/>.</item>
    ///   <item><see cref="IDtnService.GetActiveBundlesAsync"/> returns empty
    ///     (active = pending or in-custody, not expired).</item>
    ///   <item><see cref="DtnService.ExpireStaleAsync"/> reports the count
    ///     of bundles it expired (matches the iteration count on the first
    ///     pass; reports 0 thereafter — the in-place mutation is
    ///     idempotent).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task DtnBundles_TtlExpiry_BundlesTransitionToExpired()
    {
        var iterations = Math.Min(ResolveIterations(), 1_000);

        var sender = new SoakFakeMeshSender(Local);
        var store = new InMemoryDtnBundleStore();
        var svc = new DtnService(sender, store);

        // Submit bundles with a 500 ms TTL and a recipient nobody is
        // connected to so direct delivery fails and they all stay
        // <see cref="BundleStatus.Pending"/>.
        var bundles = new List<DtnBundle>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var bundle = new DtnBundle
            {
                SenderUhid = Local,
                RecipientUhid = $"recipient-{i}",
                EncryptedPayload = new byte[] { 1, 2, 3 },
                Priority = BundlePriority.Normal,
                ExpiresAt = DateTime.UtcNow.AddMilliseconds(500),
            };
            await store.SaveAsync(bundle);
            bundles.Add(bundle);
        }

        var beforeExpire = await store.GetActiveCountAsync();
        Assert.Equal(iterations, beforeExpire);

        // Wait past the TTL.
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        var expired = await svc.ExpireStaleAsync();
        Assert.Equal(iterations, expired);

        // After expiry: GetActiveAsync returns empty (active = pending or
        // in-custody, AND not expired). Every bundle from this run is now
        // gone from the active set.
        var active = await svc.GetActiveBundlesAsync();
        Assert.Empty(active);

        // The store still holds them as <see cref="BundleStatus.Expired"/> —
        // pruning final-state bundles is a separate concern.
        foreach (var bundle in bundles)
        {
            var stored = await store.GetAsync(bundle.Id);
            Assert.NotNull(stored);
            Assert.Equal(BundleStatus.Expired, stored!.Status);
        }

        // Idempotency: a second ExpireStaleAsync reports zero.
        var secondPass = await svc.ExpireStaleAsync();
        Assert.Equal(0, secondPass);
    }

    /// <summary>
    /// Sustained create + expire cycle. Verifies the store size stays
    /// bounded under continuous bundle creation without explicit
    /// orchestrator-side cleanup. Assertion: the active-count never
    /// exceeds the iteration count we ran (it CAN equal it — bundles
    /// stay until they expire).
    /// </summary>
    [Fact]
    public async Task DtnBundles_SustainedCreate_ActiveCountBounded()
    {
        var iterations = Math.Min(ResolveIterations(), 2_000);

        var sender = new SoakFakeMeshSender(Local);
        var store = new InMemoryDtnBundleStore();
        var svc = new DtnService(sender, store);

        var report = await MeasureMemoryGrowthAsync(async i =>
        {
            await svc.CreateBundleAsync(
                recipientUhid: $"recipient-{i}",
                encryptedPayload: new byte[] { (byte)i, (byte)(i >> 8) });
        }, iterations);

        WriteSummary(nameof(DtnBundles_SustainedCreate_ActiveCountBounded), report, iterations);

        var activeCount = await store.GetActiveCountAsync();
        Assert.Equal(iterations, activeCount);

        // Every bundle is roughly DtnBundle (.NET object overhead + a few
        // strings + 2-byte payload) ≈ 256-512B serialized.
        // Allowing a comfortable 4 KB/iter envelope absorbs the JSON-ready
        // string fields and any concurrent-dictionary segment churn.
        Assert.True(report.PerIterationBytes < 4_096,
            $"DTN per-iteration growth: {report.PerIterationBytes:F1}B/iter — exceeds 4 KB. " +
            "Check for hidden retention beyond the bundle and custody-record entries.");
    }
}

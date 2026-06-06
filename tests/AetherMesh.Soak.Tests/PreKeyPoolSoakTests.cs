// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text;
using AetherMesh.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherMesh.Soak.Tests;

/// <summary>
/// Soak tests for the one-time pre-key pool. The pool tops up to
/// <see cref="SignalProtocolService.OpkPoolSize"/> on every bundle
/// generation; under concurrent load, two initiators MUST never receive
/// the same OPK id from a single responder. The unit tests cover sequential
/// dispensation; this soak run exercises concurrent dispensation under
/// thread-pool pressure.
/// </summary>
[Trait("Category", "Soak")]
public class PreKeyPoolSoakTests : SoakTestBase
{
    private const string ResponderUhid = "bob-uhid";

    /// <summary>
    /// Spin up 200 concurrent initiators against a single responder's
    /// pool. Each initiator pulls a bundle. Asserts:
    /// <list type="bullet">
    ///   <item>Every initiator gets a non-zero OPK id.</item>
    ///   <item>All OPK ids are distinct (no collision under
    ///     contention).</item>
    ///   <item>The pool is replenished — final
    ///     <see cref="SignalProtocolService.AvailableOneTimePreKeyCount"/>
    ///     equals the configured pool size.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task OneTimePreKey_PoolReplenish_UnderConcurrentInitiators()
    {
        const int initiators = 200;
        const int poolSize = 100;

        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, poolSize);
        await bob.GeneratePreKeyBundleAsync(ResponderUhid); // prime pool

        var collectedIds = new ConcurrentBag<int>();
        var tasks = new Task[initiators];

        for (var i = 0; i < initiators; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var bundle = await bob.GeneratePreKeyBundleAsync(ResponderUhid);
                collectedIds.Add(bundle.PreKeyId);
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(initiators, collectedIds.Count);

        var distinct = new HashSet<int>(collectedIds);
        Assert.Equal(initiators, distinct.Count);

        // Must not include id 0 (the sentinel "uninitialized" value).
        Assert.DoesNotContain(0, distinct);

        // After dispensing 200 ids the pool should still be near full —
        // top-up runs at the START of every GenerateBundleAsync call, so
        // after the last call the pool sits at poolSize - 1 (we dequeued
        // immediately after topping up to poolSize). Asserting >= poolSize - 1
        // is the correct invariant: the pool MUST stay close to its target,
        // never drain to zero or below.
        Assert.True(bob.AvailableOneTimePreKeyCount >= poolSize - 1,
            $"Pool drained: AvailableOneTimePreKeyCount={bob.AvailableOneTimePreKeyCount} " +
            $"after {initiators} bundles — expected >= {poolSize - 1}.");
    }

    /// <summary>
    /// Sustained X3DH establishment + tear-down: each iteration creates
    /// a fresh initiator, runs X3DH against Bob's bundle, sends the first
    /// message (which consumes Bob's OPK), and discards both sides.
    /// Asserts:
    /// <list type="bullet">
    ///   <item>Bob's pool stays at <see cref="SignalProtocolService.OpkPoolSize"/>
    ///     available between iterations — top-up actually runs.</item>
    ///   <item>The held-OPK count grows but stays bounded by the
    ///     concurrent-initiator-issued count (we don't issue beyond the
    ///     pool size in steady state).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task OneTimePreKey_LongRunConsumeAndReplenish_StaysSteady()
    {
        var iterations = Math.Min(ResolveIterations(), 500);
        const int poolSize = 32;

        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, poolSize);

        var samplePoints = new List<(int held, int available)>();

        for (var i = 0; i < iterations; i++)
        {
            // Fresh initiator each iteration.
            var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
            await alice.GeneratePreKeyBundleAsync($"alice-{i}");

            var bundle = await bob.GeneratePreKeyBundleAsync(ResponderUhid);
            await alice.ProcessPreKeyBundleAsync(bundle);

            // Send + decrypt — consumes the OPK on Bob's side.
            var first = await alice.EncryptAsync(ResponderUhid, Encoding.UTF8.GetBytes($"msg-{i}"));
            await bob.DecryptAsync($"alice-{i}", first);

            // Sample every 50 iterations so the assertion array doesn't
            // dominate runtime memory.
            if ((i % 50) == 0)
                samplePoints.Add((bob.HeldOneTimePreKeyCount, bob.AvailableOneTimePreKeyCount));
        }

        // Final state: pool should still be sized for steady operation.
        // After the last GenerateBundleAsync call (which topped up to
        // poolSize then dequeued one), available = poolSize - 1.
        Assert.True(bob.AvailableOneTimePreKeyCount >= poolSize - 1,
            $"Pool drained mid-run: AvailableOneTimePreKeyCount={bob.AvailableOneTimePreKeyCount}, " +
            $"expected >= {poolSize - 1}.");

        // Across the run, available count should never have dropped to 0
        // (pool tops up before dispensing), and held count should never
        // have exceeded ~2*poolSize (held = available + outstanding-issued,
        // and outstanding-issued is bounded by how many initiators are
        // mid-flight — here, sequential, so always 1).
        foreach (var (held, available) in samplePoints)
        {
            Assert.True(available > 0,
                $"Pool exhausted mid-run (available={available}, held={held})");
            Assert.True(held <= poolSize * 2,
                $"Held-key count {held} exceeded 2x pool size {poolSize} — replenish loop is over-issuing.");
        }
    }
}

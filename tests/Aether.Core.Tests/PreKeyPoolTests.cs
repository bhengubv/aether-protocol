// SPDX-License-Identifier: MIT

using System.Text;
using AetherMesh.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// Tests for the one-time pre-key pool added to
/// <see cref="SignalProtocolService"/>. Each <c>GeneratePreKeyBundleAsync</c>
/// call returns a bundle pointing to a distinct, un-issued OPK id and tops
/// the pool back up to the configured target size as keys get consumed.
///
/// Without the pool a single shared OPK id would be reused across every
/// bundle — a concurrency hazard in real deployments where many initiators
/// fetch a pre-key bundle in parallel.
/// </summary>
public class PreKeyPoolTests
{
    private static SignalProtocolService NewService(int? opkPoolSize = null) =>
        opkPoolSize is { } size
            ? new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, size)
            : new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

    [Fact]
    public async Task DefaultPoolSize_Is100()
    {
        var node = NewService();
        Assert.Equal(100, node.OpkPoolSize);
        Assert.Equal(SignalProtocolService.DefaultOpkPoolSize, node.OpkPoolSize);

        // Pool is created lazily on first GeneratePreKeyBundleAsync.
        Assert.Equal(0, node.HeldOneTimePreKeyCount);

        await node.GeneratePreKeyBundleAsync("uhid-1");

        // After issuing one bundle, the pool should be filled to target
        // (100 held), with one already-issued and 99 still available.
        Assert.Equal(100, node.HeldOneTimePreKeyCount);
        Assert.Equal(99, node.AvailableOneTimePreKeyCount);
    }

    [Fact]
    public async Task ConfigurablePoolSize_Honored()
    {
        var node = NewService(opkPoolSize: 8);
        Assert.Equal(8, node.OpkPoolSize);

        await node.GeneratePreKeyBundleAsync("uhid-config");

        // 8 held: 7 available + 1 issued.
        Assert.Equal(8, node.HeldOneTimePreKeyCount);
        Assert.Equal(7, node.AvailableOneTimePreKeyCount);
    }

    [Fact]
    public async Task ZeroOrNegativePoolSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewService(opkPoolSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewService(opkPoolSize: -1));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SequentialBundles_ReturnDistinctOpkIds()
    {
        var node = NewService(opkPoolSize: 100);
        var ids = new HashSet<int>();

        // Issue 100 bundles back-to-back. Each one must come back with a
        // distinct OPK id — no two concurrent initiators ever collide.
        for (var i = 0; i < 100; i++)
        {
            var bundle = await node.GeneratePreKeyBundleAsync($"uhid-{i}");
            Assert.True(ids.Add(bundle.PreKeyId),
                $"Duplicate OPK id {bundle.PreKeyId} on iteration {i} — pool collided.");
        }

        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public async Task ConsumedOpk_IsRemoved_OthersRemain()
    {
        // After a responder consumes an OPK via X3DH, that OPK should be
        // gone from the pool but every other (issued or unissued) entry
        // must remain claimable.
        var bob = NewService(opkPoolSize: 10);
        var alice = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob");
        var heldBefore = bob.HeldOneTimePreKeyCount;

        // Alice runs X3DH against the bundle and sends the first
        // (PreKey-flagged) message. Bob's DecryptAsync establishes the
        // responder session and consumes the OPK with id == bobBundle.PreKeyId.
        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bobBundle);
        var first = await alice.EncryptAsync("bob", Encoding.UTF8.GetBytes("hello"));
        await bob.DecryptAsync("alice", first);

        // Pool should now hold one fewer OPK than it did before.
        Assert.Equal(heldBefore - 1, bob.HeldOneTimePreKeyCount);
    }

    [Fact]
    public async Task TopUp_RefillsPoolOnNextBundleAfterConsumption()
    {
        var bob = NewService(opkPoolSize: 5);
        var alice1 = NewService();
        var alice2 = NewService();

        // First bundle fills the pool: 5 held, 4 available, 1 issued.
        var bundle1 = await bob.GeneratePreKeyBundleAsync("bob");
        Assert.Equal(5, bob.HeldOneTimePreKeyCount);
        Assert.Equal(4, bob.AvailableOneTimePreKeyCount);

        // Alice1 consumes the issued OPK.
        await alice1.GeneratePreKeyBundleAsync("alice1");
        await alice1.ProcessPreKeyBundleAsync(bundle1);
        var msg1 = await alice1.EncryptAsync("bob", Encoding.UTF8.GetBytes("a"));
        await bob.DecryptAsync("alice1", msg1);

        // After consumption: 4 held (5 minus the consumed one).
        Assert.Equal(4, bob.HeldOneTimePreKeyCount);

        // Next bundle generation should top up the pool back to 5 and
        // hand out a fresh, distinct id.
        var bundle2 = await bob.GeneratePreKeyBundleAsync("bob");
        Assert.NotEqual(bundle1.PreKeyId, bundle2.PreKeyId);
        Assert.Equal(5, bob.HeldOneTimePreKeyCount);
        Assert.Equal(4, bob.AvailableOneTimePreKeyCount);

        // And the second initiator using bundle2 establishes successfully.
        await alice2.GeneratePreKeyBundleAsync("alice2");
        await alice2.ProcessPreKeyBundleAsync(bundle2);
        var msg2 = await alice2.EncryptAsync("bob", Encoding.UTF8.GetBytes("b"));
        var pt = await bob.DecryptAsync("alice2", msg2);
        Assert.Equal("b", Encoding.UTF8.GetString(pt));
    }

    [Fact]
    public async Task TwoConcurrentInitiators_GetDistinctOpks_AndBothEstablish()
    {
        // The hazard the pool fixes: two initiators fetching a bundle in
        // parallel must each get their own OPK id so they don't collide
        // when their PreKey messages arrive at the responder.
        var bob = NewService(opkPoolSize: 50);

        var bundleA = await bob.GeneratePreKeyBundleAsync("bob");
        var bundleB = await bob.GeneratePreKeyBundleAsync("bob");

        Assert.NotEqual(bundleA.PreKeyId, bundleB.PreKeyId);

        var alice = NewService();
        var carol = NewService();

        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bundleA);

        await carol.GeneratePreKeyBundleAsync("carol");
        await carol.ProcessPreKeyBundleAsync(bundleB);

        var aMsg = await alice.EncryptAsync("bob", Encoding.UTF8.GetBytes("a"));
        var cMsg = await carol.EncryptAsync("bob", Encoding.UTF8.GetBytes("c"));

        Assert.Equal("a", Encoding.UTF8.GetString(await bob.DecryptAsync("alice", aMsg)));
        Assert.Equal("c", Encoding.UTF8.GetString(await bob.DecryptAsync("carol", cMsg)));
    }
}

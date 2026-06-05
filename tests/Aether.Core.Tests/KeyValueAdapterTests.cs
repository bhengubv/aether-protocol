// SPDX-License-Identifier: MIT

using AetherMesh.Messaging.Models;
using AetherMesh.Models;
using AetherMesh.Storage;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// Unit tests for the three KV-backed store adapters in <c>Aether.Storage</c>:
/// <see cref="KeyValueDtnBundleStore"/>, <see cref="KeyValueMessageStore"/>, and
/// <see cref="KeyValueRouteStore"/>. All tests run against
/// <see cref="InMemoryKeyValueStore"/> — the underlying KV abstraction is exercised
/// separately in <see cref="InMemoryKeyValueStoreTests"/> and
/// <see cref="FileSystemKeyValueStoreTests"/>, so here we focus on JSON
/// serialisation, the prefix-key scheme, and lifecycle queries.
/// </summary>
public class KeyValueAdapterTests
{
    // ─── DTN Bundle Store ────────────────────────────────────────

    [Fact]
    public async Task DtnBundleStore_SaveThenGet_RoundTripsBundle()
    {
        var store = new KeyValueDtnBundleStore(new InMemoryKeyValueStore());
        var bundle = new DtnBundle
        {
            SenderUhid = "sender-uhid",
            RecipientUhid = "recipient-uhid",
            EncryptedPayload = new byte[] { 1, 2, 3, 4 },
            Priority = BundlePriority.High,
            Status = BundleStatus.Pending,
            HopCount = 2,
        };

        await store.SaveAsync(bundle);
        var got = await store.GetAsync(bundle.Id);

        Assert.NotNull(got);
        Assert.Equal(bundle.Id, got!.Id);
        Assert.Equal("sender-uhid", got.SenderUhid);
        Assert.Equal("recipient-uhid", got.RecipientUhid);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, got.EncryptedPayload);
        Assert.Equal(BundlePriority.High, got.Priority);
        Assert.Equal(BundleStatus.Pending, got.Status);
        Assert.Equal(2, got.HopCount);
    }

    [Fact]
    public async Task DtnBundleStore_GetActive_ReturnsOnlyPendingAndInCustody()
    {
        var store = new KeyValueDtnBundleStore(new InMemoryKeyValueStore());

        var pending = new DtnBundle { Status = BundleStatus.Pending };
        var inCustody = new DtnBundle { Status = BundleStatus.InCustody };
        var delivered = new DtnBundle { Status = BundleStatus.Delivered };
        var failed = new DtnBundle { Status = BundleStatus.Failed };

        await store.SaveAsync(pending);
        await store.SaveAsync(inCustody);
        await store.SaveAsync(delivered);
        await store.SaveAsync(failed);

        var active = await store.GetActiveAsync();
        var ids = active.Select(b => b.Id).ToHashSet();

        Assert.Equal(2, active.Count);
        Assert.Contains(pending.Id, ids);
        Assert.Contains(inCustody.Id, ids);
        Assert.Equal(2, await store.GetActiveCountAsync());
    }

    [Fact]
    public async Task DtnBundleStore_Remove_DeletesBundle()
    {
        var store = new KeyValueDtnBundleStore(new InMemoryKeyValueStore());
        var bundle = new DtnBundle { Status = BundleStatus.Pending };
        await store.SaveAsync(bundle);

        await store.RemoveAsync(bundle.Id);

        Assert.Null(await store.GetAsync(bundle.Id));
    }

    [Fact]
    public async Task DtnBundleStore_CustodyRecords_AreScopedByBundle()
    {
        var store = new KeyValueDtnBundleStore(new InMemoryKeyValueStore());
        var bundleA = Guid.NewGuid();
        var bundleB = Guid.NewGuid();

        await store.SaveCustodyAsync(new CustodyRecord { BundleId = bundleA, FromUhid = "x", ToUhid = "y", Accepted = true });
        await store.SaveCustodyAsync(new CustodyRecord { BundleId = bundleA, FromUhid = "y", ToUhid = "z", Accepted = true });
        await store.SaveCustodyAsync(new CustodyRecord { BundleId = bundleB, FromUhid = "p", ToUhid = "q", Accepted = false });

        var aRecords = await store.GetCustodyRecordsAsync(bundleA);
        var bRecords = await store.GetCustodyRecordsAsync(bundleB);

        Assert.Equal(2, aRecords.Count);
        Assert.Single(bRecords);
        Assert.All(aRecords, r => Assert.Equal(bundleA, r.BundleId));
    }

    [Fact]
    public async Task DtnBundleStore_ExpireStale_MarksOnlyPastExpiry()
    {
        var store = new KeyValueDtnBundleStore(new InMemoryKeyValueStore());
        var fresh = new DtnBundle { Status = BundleStatus.Pending, ExpiresAt = DateTime.UtcNow.AddHours(1) };
        var stale = new DtnBundle { Status = BundleStatus.Pending, ExpiresAt = DateTime.UtcNow.AddSeconds(-1) };

        await store.SaveAsync(fresh);
        await store.SaveAsync(stale);

        var expired = await store.ExpireStaleAsync();

        Assert.Equal(1, expired);
        Assert.Equal(BundleStatus.Pending, (await store.GetAsync(fresh.Id))!.Status);
        Assert.Equal(BundleStatus.Expired, (await store.GetAsync(stale.Id))!.Status);
    }

    // ─── Message Store ───────────────────────────────────────────

    [Fact]
    public async Task MessageStore_SaveThenGet_RoundTripsMessage()
    {
        var store = new KeyValueMessageStore(new InMemoryKeyValueStore());
        var message = new MeshMessage
        {
            SenderUhid = "alice",
            RecipientUhid = "bob",
            EncryptedContent = new byte[] { 9, 8, 7 },
            MessageType = "text",
            Priority = 5,
            Status = MessageStatus.Pending,
        };

        await store.SaveAsync(message);
        var got = await store.GetAsync(message.Id);

        Assert.NotNull(got);
        Assert.Equal(message.Id, got!.Id);
        Assert.Equal("alice", got.SenderUhid);
        Assert.Equal("bob", got.RecipientUhid);
        Assert.Equal(new byte[] { 9, 8, 7 }, got.EncryptedContent);
        Assert.Equal("text", got.MessageType);
        Assert.Equal((byte)5, got.Priority);
        Assert.Equal(MessageStatus.Pending, got.Status);
    }

    [Fact]
    public async Task MessageStore_UpdateStatusAndIncrementRetry_PersistChanges()
    {
        var store = new KeyValueMessageStore(new InMemoryKeyValueStore());
        var message = new MeshMessage { SenderUhid = "alice", RecipientUhid = "bob" };
        await store.SaveAsync(message);

        await store.UpdateStatusAsync(message.Id, MessageStatus.Sent);
        await store.IncrementRetryAsync(message.Id);
        await store.IncrementRetryAsync(message.Id);

        var got = await store.GetAsync(message.Id);
        Assert.NotNull(got);
        Assert.Equal(MessageStatus.Sent, got!.Status);
        Assert.Equal(2, got.RetryCount);
    }

    [Fact]
    public async Task MessageStore_GetPendingOutbox_FiltersBySenderStatusAndRetryBudget()
    {
        var store = new KeyValueMessageStore(new InMemoryKeyValueStore());

        var alicePending = new MeshMessage { SenderUhid = "alice", Status = MessageStatus.Pending, RetryCount = 0 };
        var aliceSending = new MeshMessage { SenderUhid = "alice", Status = MessageStatus.Sending, RetryCount = 1 };
        var aliceExhausted = new MeshMessage { SenderUhid = "alice", Status = MessageStatus.Pending, RetryCount = 5 };
        var aliceDelivered = new MeshMessage { SenderUhid = "alice", Status = MessageStatus.Delivered, RetryCount = 0 };
        var bobPending = new MeshMessage { SenderUhid = "bob", Status = MessageStatus.Pending, RetryCount = 0 };

        foreach (var m in new[] { alicePending, aliceSending, aliceExhausted, aliceDelivered, bobPending })
            await store.SaveAsync(m);

        var pending = await store.GetPendingOutboxAsync("alice", maxRetries: 5);
        var ids = pending.Select(m => m.Id).ToHashSet();

        Assert.Equal(2, pending.Count);
        Assert.Contains(alicePending.Id, ids);
        Assert.Contains(aliceSending.Id, ids);
    }

    [Fact]
    public async Task MessageStore_GetInbox_ReturnsRecipientMessagesNewestFirstWithLimit()
    {
        var store = new KeyValueMessageStore(new InMemoryKeyValueStore());
        var now = DateTime.UtcNow;

        var older = new MeshMessage { RecipientUhid = "bob", CreatedAt = now.AddMinutes(-10) };
        var middle = new MeshMessage { RecipientUhid = "bob", CreatedAt = now.AddMinutes(-5) };
        var newest = new MeshMessage { RecipientUhid = "bob", CreatedAt = now };
        var noise = new MeshMessage { RecipientUhid = "carol", CreatedAt = now };

        foreach (var m in new[] { older, middle, newest, noise }) await store.SaveAsync(m);

        var inbox = await store.GetInboxAsync("bob", limit: 2);

        Assert.Equal(2, inbox.Count);
        Assert.Equal(newest.Id, inbox[0].Id);
        Assert.Equal(middle.Id, inbox[1].Id);
    }

    // ─── Route Store ─────────────────────────────────────────────

    [Fact]
    public async Task RouteStore_SaveThenGet_RoundTripsRoute()
    {
        var store = new KeyValueRouteStore(new InMemoryKeyValueStore());
        var route = new RouteEntry
        {
            DestinationUhid = "dest-uhid",
            NextHopUhid = "next-uhid",
            HopCount = 3,
            LatencyMs = 42.5,
            QualityScore = 0.85,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };

        await store.SaveAsync(route);
        var got = await store.GetAsync("dest-uhid");

        Assert.NotNull(got);
        Assert.Equal("dest-uhid", got!.DestinationUhid);
        Assert.Equal("next-uhid", got.NextHopUhid);
        Assert.Equal(3, got.HopCount);
        Assert.Equal(42.5, got.LatencyMs);
        Assert.Equal(0.85, got.QualityScore);
    }

    [Fact]
    public async Task RouteStore_RemoveAndGetAll_BehaveCorrectly()
    {
        var store = new KeyValueRouteStore(new InMemoryKeyValueStore());

        await store.SaveAsync(new RouteEntry { DestinationUhid = "alice", NextHopUhid = "hop1" });
        await store.SaveAsync(new RouteEntry { DestinationUhid = "bob", NextHopUhid = "hop2" });
        await store.SaveAsync(new RouteEntry { DestinationUhid = "carol", NextHopUhid = "hop3" });

        var all = await store.GetAllAsync();
        Assert.Equal(3, all.Count);

        await store.RemoveAsync("bob");

        var afterRemoval = await store.GetAllAsync();
        Assert.Equal(2, afterRemoval.Count);
        Assert.DoesNotContain(afterRemoval, r => r.DestinationUhid == "bob");
        Assert.Null(await store.GetAsync("bob"));
    }

    [Fact]
    public async Task RouteStore_PruneExpired_RemovesOnlyStaleRoutes()
    {
        var store = new KeyValueRouteStore(new InMemoryKeyValueStore());

        var fresh = new RouteEntry { DestinationUhid = "fresh", NextHopUhid = "hop", ExpiresAt = DateTime.UtcNow.AddMinutes(5) };
        var stale = new RouteEntry { DestinationUhid = "stale", NextHopUhid = "hop", ExpiresAt = DateTime.UtcNow.AddSeconds(-1) };

        await store.SaveAsync(fresh);
        await store.SaveAsync(stale);

        var pruned = await store.PruneExpiredAsync();

        Assert.Equal(1, pruned);
        Assert.NotNull(await store.GetAsync("fresh"));
        Assert.Null(await store.GetAsync("stale"));
    }
}

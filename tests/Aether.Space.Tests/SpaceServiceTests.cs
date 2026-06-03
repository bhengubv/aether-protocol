// SPDX-License-Identifier: MIT
using Aether.Space;
using Aether.Space.Models;
using Xunit;

namespace Aether.Space.Tests;

public sealed class SpaceServiceTests
{
    // ── 1. DropAsync creates a valid SpaceBreadcrumb with correct fields ──────

    [Fact]
    public async Task DropAsync_CreatesValidBreadcrumbWithCorrectFields()
    {
        var svc = new InMemorySpaceService();
        var before = DateTime.UtcNow;

        var crumb = await svc.DropAsync(
            geoHash: "u33dbq",
            contentHash: "sha256:abc123",
            anchorUhid: "aether:node:01",
            type: BreadcrumbType.Notice,
            ttlHours: 48);

        Assert.Equal("u33dbq", crumb.GeoHash);
        Assert.Equal("sha256:abc123", crumb.ContentHash);
        Assert.Equal("aether:node:01", crumb.AnchorUhid);
        Assert.Equal(BreadcrumbType.Notice, crumb.Type);
        Assert.Equal(48, crumb.TtlHours);
        Assert.True(crumb.CreatedAtUtc >= before);
        Assert.False(crumb.IsExpired);
    }

    // ── 2. DropAsync Emergency type gets TtlHours=720 regardless of input ────

    [Fact]
    public async Task DropAsync_EmergencyType_GetsTtlOf720()
    {
        var svc = new InMemorySpaceService();

        var crumb = await svc.DropAsync(
            geoHash: "u33dbq",
            contentHash: "sha256:emergency1",
            anchorUhid: "aether:node:02",
            type: BreadcrumbType.Emergency,
            ttlHours: 24);   // should be ignored

        Assert.Equal(720, crumb.TtlHours);
    }

    // ── 3. DropAsync normal ttlHours is clamped to [1, 168] ──────────────────

    [Fact]
    public async Task DropAsync_TtlHours_IsClampedToValidRange()
    {
        var svc = new InMemorySpaceService();

        // Below min → clamp to 1
        var low = await svc.DropAsync("u33dbq", "sha256:low", "aether:node:03",
            ttlHours: 0);
        Assert.Equal(1, low.TtlHours);

        // Above max → clamp to 168
        var high = await svc.DropAsync("u33dbq", "sha256:high", "aether:node:03",
            ttlHours: 999);
        Assert.Equal(168, high.TtlHours);

        // In range → unchanged
        var normal = await svc.DropAsync("u33dbq", "sha256:normal", "aether:node:03",
            ttlHours: 72);
        Assert.Equal(72, normal.TtlHours);
    }

    // ── 4. ScanAsync returns breadcrumbs within the geohash prefix ────────────

    [Fact]
    public async Task ScanAsync_ReturnsBreadcrumbsWithinGeohashPrefix()
    {
        var svc = new InMemorySpaceService();

        await svc.DropAsync("u33dbq", "sha256:a", "node:1");   // same prefix
        await svc.DropAsync("u33dbr", "sha256:b", "node:2");   // different last char — still same 5-prefix
        await svc.DropAsync("9q8yy7", "sha256:c", "node:3");   // completely different

        // radiusCells=1 → prefix length 5: "u33db"
        var results = await svc.ScanAsync("u33dbq", radiusCells: 1);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.ContentHash == "sha256:a");
        Assert.Contains(results, r => r.ContentHash == "sha256:b");
        Assert.DoesNotContain(results, r => r.ContentHash == "sha256:c");
    }

    // ── 5. ScanAsync excludes expired breadcrumbs ─────────────────────────────

    [Fact]
    public async Task ScanAsync_ExcludesExpiredBreadcrumbs()
    {
        var svc = new InMemorySpaceService();

        // Drop an already-expired breadcrumb by back-dating CreatedAtUtc via PinAsync
        var expired = new SpaceBreadcrumb
        {
            ContentHash  = "sha256:expired",
            GeoHash      = "u33dbq",
            AnchorUhid   = "node:1",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-200),
            TtlHours     = 1,
            Type         = BreadcrumbType.Notice,
        };
        await svc.PinAsync(expired);

        var active = await svc.DropAsync("u33dbq", "sha256:active", "node:2", ttlHours: 72);

        var results = await svc.ScanAsync("u33dbq");

        Assert.DoesNotContain(results, r => r.ContentHash == "sha256:expired");
        Assert.Contains(results, r => r.ContentHash == "sha256:active");
    }

    // ── 6. ScanAsync with wider radius returns more results ───────────────────

    [Fact]
    public async Task ScanAsync_WiderRadius_ReturnsMoreResults()
    {
        var svc = new InMemorySpaceService();

        // These share different 5-char prefixes but same 3-char prefix "u33"
        await svc.DropAsync("u33dbq", "sha256:p1", "node:1");
        await svc.DropAsync("u33xyz", "sha256:p2", "node:2");
        await svc.DropAsync("9q8yy7", "sha256:p3", "node:3");   // different continent

        // Narrow scan (radius=1, prefix len 5) — only "u33db" matches
        var narrow = await svc.ScanAsync("u33dbq", radiusCells: 1);

        // Wider scan (radius=3, prefix len 3) — "u33" matches both
        var wider = await svc.ScanAsync("u33dbq", radiusCells: 3);

        Assert.True(wider.Count >= narrow.Count);
        Assert.Contains(wider, r => r.ContentHash == "sha256:p2");
    }

    // ── 7. DeleteAsync returns true when creator deletes their own breadcrumb ─

    [Fact]
    public async Task DeleteAsync_ReturnsTrue_WhenCreatorDeletes()
    {
        var svc = new InMemorySpaceService();
        var crumb = await svc.DropAsync("u33dbq", "sha256:del1", "aether:owner");

        var result = await svc.DeleteAsync(crumb, requestorUhid: "aether:owner");

        Assert.True(result);

        // Confirm it's gone from the store
        var results = await svc.ScanAsync("u33dbq");
        Assert.DoesNotContain(results, r => r.ContentHash == "sha256:del1");
    }

    // ── 8. DeleteAsync returns false when a non-creator attempts to delete ────

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenNonCreatorAttempts()
    {
        var svc = new InMemorySpaceService();
        var crumb = await svc.DropAsync("u33dbq", "sha256:del2", "aether:owner");

        var result = await svc.DeleteAsync(crumb, requestorUhid: "aether:impostor");

        Assert.False(result);

        // Breadcrumb must still be in the store
        var results = await svc.ScanAsync("u33dbq");
        Assert.Contains(results, r => r.ContentHash == "sha256:del2");
    }

    // ── 9. DeleteAsync returns false for a non-existent breadcrumb ───────────

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_ForNonExistentBreadcrumb()
    {
        var svc = new InMemorySpaceService();

        var ghost = new SpaceBreadcrumb
        {
            ContentHash = "sha256:ghost",
            GeoHash     = "u33dbq",
            AnchorUhid  = "aether:owner",
        };

        var result = await svc.DeleteAsync(ghost, requestorUhid: "aether:owner");

        Assert.False(result);
    }

    // ── 10. PruneExpired removes expired breadcrumbs and fires BreadcrumbExpired

    [Fact]
    public async Task PruneExpired_RemovesExpiredBreadcrumbs_AndFiresEvent()
    {
        var svc = new InMemorySpaceService();

        var expiredCrumbs = new List<SpaceBreadcrumb>();
        ((ISpaceService)svc).BreadcrumbExpired += (_, b) => expiredCrumbs.Add(b);

        // Pin two back-dated (expired) breadcrumbs
        await svc.PinAsync(new SpaceBreadcrumb
        {
            ContentHash  = "sha256:exp1",
            GeoHash      = "u33dbq",
            AnchorUhid   = "node:1",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-200),
            TtlHours     = 1,
        });
        await svc.PinAsync(new SpaceBreadcrumb
        {
            ContentHash  = "sha256:exp2",
            GeoHash      = "u33dbq",
            AnchorUhid   = "node:2",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-200),
            TtlHours     = 1,
        });

        // And one valid breadcrumb
        await svc.DropAsync("u33dbq", "sha256:live", "node:3", ttlHours: 72);

        var pruned = svc.PruneExpired();

        Assert.Equal(2, pruned);
        Assert.Equal(2, expiredCrumbs.Count);
        Assert.Contains(expiredCrumbs, b => b.ContentHash == "sha256:exp1");
        Assert.Contains(expiredCrumbs, b => b.ContentHash == "sha256:exp2");

        // Live breadcrumb must survive
        var results = await svc.ScanAsync("u33dbq");
        Assert.Contains(results, r => r.ContentHash == "sha256:live");
    }

    // ── 11. PinAsync stores an external breadcrumb and fires BreadcrumbReceived

    [Fact]
    public async Task PinAsync_StoresExternalBreadcrumb_AndFiresEvent()
    {
        var svc = new InMemorySpaceService();

        SpaceBreadcrumb? received = null;
        ((ISpaceService)svc).BreadcrumbReceived += (_, b) => received = b;

        var external = new SpaceBreadcrumb
        {
            ContentHash  = "sha256:pinned",
            GeoHash      = "u33dbq",
            AnchorUhid   = "aether:remote",
            CreatedAtUtc = DateTime.UtcNow,
            TtlHours     = 72,
        };

        await svc.PinAsync(external);

        Assert.NotNull(received);
        Assert.Equal("sha256:pinned", received!.ContentHash);

        var results = await svc.ScanAsync("u33dbq");
        Assert.Contains(results, r => r.ContentHash == "sha256:pinned");
    }

    // ── 12. BreadcrumbReceived event fires on DropAsync ───────────────────────

    [Fact]
    public async Task DropAsync_FiresBreadcrumbReceivedEvent()
    {
        var svc = new InMemorySpaceService();

        SpaceBreadcrumb? received = null;
        ((ISpaceService)svc).BreadcrumbReceived += (_, b) => received = b;

        await svc.DropAsync("u33dbq", "sha256:event1", "aether:node:ev");

        Assert.NotNull(received);
        Assert.Equal("sha256:event1", received!.ContentHash);
        Assert.Equal("aether:node:ev", received.AnchorUhid);
    }
}

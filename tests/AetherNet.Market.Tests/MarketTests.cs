// SPDX-License-Identifier: MIT
using AetherNet.Market;
using AetherNet.Market.Models;
using Xunit;

namespace AetherNet.Market.Tests;

public sealed class MarketTests
{
    // ── PoV Tests ─────────────────────────────────────────────────────────────

    // 1. IssueTokenAsync returns token with correct UHIDs
    [Fact]
    public async Task IssueTokenAsync_ReturnsTokenWithCorrectUhids()
    {
        var svc = new InMemoryPoVService();

        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        Assert.Equal("witness:01", token.WitnessUhid);
        Assert.Equal("subject:02", token.SubjectUhid);
    }

    // 2. AcceptTokenAsync increments UniqueWitnesses
    [Fact]
    public async Task AcceptTokenAsync_IncrementsUniqueWitnesses()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        await svc.AcceptTokenAsync(token);

        var score = await svc.GetScoreAsync("subject:02");
        Assert.Equal(1, score.UniqueWitnesses);
    }

    // 3. Same witness twice — counts once
    [Fact]
    public async Task AcceptTokenAsync_SameWitnessTwice_CountsOnce()
    {
        var svc = new InMemoryPoVService();
        var token1 = await svc.IssueTokenAsync("witness:01", "subject:02");
        var token2 = await svc.IssueTokenAsync("witness:01", "subject:02");

        await svc.AcceptTokenAsync(token1);
        await svc.AcceptTokenAsync(token2);

        var score = await svc.GetScoreAsync("subject:02");
        Assert.Equal(1, score.UniqueWitnesses);
    }

    // 4. Unknown UHID returns zero score
    [Fact]
    public async Task GetScoreAsync_UnknownUhid_ReturnsZeroScore()
    {
        var svc = new InMemoryPoVService();

        var score = await svc.GetScoreAsync("nobody:99");

        Assert.Equal(0, score.UniqueWitnesses);
        Assert.Equal(0.0, score.WeightedScore);
    }

    // 5. VerifyTokenAsync — valid token — returns true
    [Fact]
    public async Task VerifyTokenAsync_ValidToken_ReturnsTrue()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        var result = await svc.VerifyTokenAsync(token);

        Assert.True(result);
    }

    // 6. VerifyTokenAsync — empty signature — returns false
    [Fact]
    public async Task VerifyTokenAsync_EmptySignature_ReturnsFalse()
    {
        var svc = new InMemoryPoVService();
        var token = new PoVToken
        {
            WitnessUhid      = "witness:01",
            SubjectUhid      = "subject:02",
            WitnessSignature = [],          // empty — invalid
            SubjectSignature = new byte[32],
        };

        var result = await svc.VerifyTokenAsync(token);

        Assert.False(result);
    }

    // 7. ReportDefectionAsync reduces witness WeightedScore by 20%
    [Fact]
    public async Task ReportDefectionAsync_ReducesWitnessScore()
    {
        var svc = new InMemoryPoVService();

        // Give the witness a score by accepting tokens for them first.
        // We do this by making witness:01 also a subject vouched for by someone else.
        var boostToken = await svc.IssueTokenAsync("booster:00", "witness:01");
        await svc.AcceptTokenAsync(boostToken);

        var scoreBefore = await svc.GetScoreAsync("witness:01");
        var expectedBefore = scoreBefore.WeightedScore;   // 1/(1+1) = 0.5

        await svc.ReportDefectionAsync("witness:01", "defector:03");

        var scoreAfter = await svc.GetScoreAsync("witness:01");
        Assert.Equal(expectedBefore * 0.8, scoreAfter.WeightedScore, precision: 10);
    }

    // 8. TokenReceived event fires on AcceptTokenAsync
    [Fact]
    public async Task TokenReceived_EventFires_OnAccept()
    {
        var svc = new InMemoryPoVService();
        PoVToken? received = null;
        ((IPoVService)svc).TokenReceived += (_, t) => received = t;

        var token = await svc.IssueTokenAsync("witness:01", "subject:02");
        received = null; // reset after IssueTokenAsync fire

        await svc.AcceptTokenAsync(token);

        Assert.NotNull(received);
        Assert.Equal("subject:02", received.SubjectUhid);
    }

    // ── Real Ed25519 — sign + verify, tamper detection ───────────────────────

    // 8a. An issued token carries 64-byte Ed25519 signatures (not random 32-byte blobs).
    [Fact]
    public async Task IssueTokenAsync_ProducesRealEd25519Signatures()
    {
        var svc = new InMemoryPoVService();

        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        Assert.Equal(64, token.WitnessSignature.Length); // Ed25519 signatures are exactly 64 bytes
        Assert.Equal(64, token.SubjectSignature.Length);
    }

    // 8b. Tampering with the subject UHID after signing INVALIDATES the signature.
    [Fact]
    public async Task VerifyTokenAsync_TamperedSubject_FailsVerification()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        // Real signature verifies before tampering.
        Assert.True(await svc.VerifyTokenAsync(token));

        // Tamper: the canonical signable body covers SubjectUhid — change it and the signature no longer matches.
        token.SubjectUhid = "attacker:99";

        Assert.False(await svc.VerifyTokenAsync(token));
    }

    // 8c. Tampering with the timestamp INVALIDATES the signature.
    [Fact]
    public async Task VerifyTokenAsync_TamperedTimestamp_FailsVerification()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        token.TimestampUtc = token.TimestampUtc.AddSeconds(1);

        Assert.False(await svc.VerifyTokenAsync(token));
    }

    // 8d. Tampering with the transport INVALIDATES the signature.
    [Fact]
    public async Task VerifyTokenAsync_TamperedTransport_FailsVerification()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02", PoVTransportType.Ble);

        token.TransportUsed = PoVTransportType.Nfc;

        Assert.False(await svc.VerifyTokenAsync(token));
    }

    // 8e. A garbage 64-byte signature does NOT verify (real crypto, not a length check).
    [Fact]
    public async Task VerifyTokenAsync_GarbageSignature_FailsVerification()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02");

        token.WitnessSignature = new byte[64]; // 64 zero bytes — well-formed length, invalid signature

        Assert.False(await svc.VerifyTokenAsync(token));
    }

    // 8f. witness == subject is rejected (no self-vouching).
    [Fact]
    public async Task VerifyTokenAsync_WitnessEqualsSubject_FailsVerification()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("same:01", "same:01");

        Assert.False(await svc.VerifyTokenAsync(token));
    }

    // 8g. A tampered token is NOT recorded by AcceptTokenAsync (score stays zero).
    [Fact]
    public async Task AcceptTokenAsync_TamperedToken_NotRecorded()
    {
        var svc = new InMemoryPoVService();
        var token = await svc.IssueTokenAsync("witness:01", "subject:02");
        token.SubjectUhid = "victim:99"; // breaks the signature

        await svc.AcceptTokenAsync(token);

        var score = await svc.GetScoreAsync("victim:99");
        Assert.Equal(0, score.UniqueWitnesses);
    }

    // ── Market Tests ──────────────────────────────────────────────────────────

    // 9. CreateListingAsync stores and returns listing
    [Fact]
    public async Task CreateListingAsync_StoresAndReturnsListing()
    {
        var svc = new InMemoryMarketService();

        var listing = await svc.CreateListingAsync(
            "seller:01", "Test Widget", "A great widget", 99.99m, "e5hj7b", MarketCategory.Goods);

        Assert.NotEqual(Guid.Empty, listing.ListingId);
        Assert.Equal("seller:01", listing.SellerUhid);
        Assert.Equal("Test Widget", listing.Title);
        Assert.Equal(99.99m, listing.PriceZAR);
        Assert.Equal("e5hj7b", listing.GeoHash);
        Assert.Equal(MarketCategory.Goods, listing.Category);
    }

    // 10. BrowseNearbyAsync returns by geohash prefix
    [Fact]
    public async Task BrowseNearbyAsync_ReturnsByGeoHash()
    {
        var svc = new InMemoryMarketService();

        await svc.CreateListingAsync("seller:01", "Near item",   "desc", 10m, "e5hj7b", MarketCategory.Goods);
        await svc.CreateListingAsync("seller:02", "Far item",    "desc", 20m, "u4pruv", MarketCategory.Goods);
        await svc.CreateListingAsync("seller:03", "Medium item", "desc", 30m, "e5hj2q", MarketCategory.Services);

        // centerGeoHash = "e5hj7b", radiusCells = 2 → prefixLen = max(1, 6-2+1)=5 → "e5hj7"
        var results = await svc.BrowseNearbyAsync("e5hj7b", radiusCells: 2);

        Assert.Contains(results, l => l.Title == "Near item");
        Assert.DoesNotContain(results, l => l.Title == "Far item");
    }

    // 11. SearchAsync finds by title
    [Fact]
    public async Task SearchAsync_FindsByTitle()
    {
        var svc = new InMemoryMarketService();

        await svc.CreateListingAsync("seller:01", "Fresh Maize",    "corn",  15m, "e5hj7b", MarketCategory.Goods);
        await svc.CreateListingAsync("seller:02", "Plumbing repair", "pipes", 500m, "e5hj7b", MarketCategory.Services);

        var results = await svc.SearchAsync("maize");

        Assert.Single(results);
        Assert.Equal("Fresh Maize", results[0].Title);
    }

    // 12. InitiateTradeAsync creates escrow in Initiated state
    [Fact]
    public async Task InitiateTradeAsync_CreatesEscrowInInitiatedState()
    {
        var svc = new InMemoryMarketService();
        var listing = await svc.CreateListingAsync(
            "seller:01", "Item", "desc", 50m, "e5hj7b", MarketCategory.Goods);

        var escrow = await svc.InitiateTradeAsync(listing, "buyer:42");

        Assert.NotEqual(Guid.Empty, escrow.EscrowId);
        Assert.Equal(listing.ListingId, escrow.ListingId);
        Assert.Equal("buyer:42",  escrow.BuyerUhid);
        Assert.Equal("seller:01", escrow.SellerUhid);
        Assert.Equal(TradeState.Initiated, escrow.State);
    }

    // 13. ConfirmTradeAsync — both parties confirm — reaches Complete state
    [Fact]
    public async Task ConfirmTradeAsync_BothParties_ReachesCompleteState()
    {
        var svc = new InMemoryMarketService();
        var listing = await svc.CreateListingAsync(
            "seller:01", "Item", "desc", 50m, "e5hj7b", MarketCategory.Goods);
        var escrow = await svc.InitiateTradeAsync(listing, "buyer:42");

        var afterBuyer = await svc.ConfirmTradeAsync(escrow, TradeRole.Buyer);
        Assert.Equal(TradeState.BuyerConfirmed, afterBuyer.State);

        var afterSeller = await svc.ConfirmTradeAsync(afterBuyer, TradeRole.Seller);
        Assert.Equal(TradeState.Complete, afterSeller.State);
    }

    // 14. DisputeAsync sets Disputed state
    [Fact]
    public async Task DisputeAsync_SetsDisputedState()
    {
        var svc = new InMemoryMarketService();
        var listing = await svc.CreateListingAsync(
            "seller:01", "Item", "desc", 50m, "e5hj7b", MarketCategory.Goods);
        var escrow = await svc.InitiateTradeAsync(listing, "buyer:42");

        await svc.DisputeAsync(escrow, "Item not as described.");

        Assert.Equal(TradeState.Disputed, escrow.State);
    }
}

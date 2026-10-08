// SPDX-License-Identifier: MIT

using AetherNet.Market;
using AetherNet.Market.Models;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// A neighbourhood marketplace with escrow: the Market page's demo, run here with the real in-memory marketplace while
/// the page shows it.
/// </summary>
/// <remarks>
/// The page's own code, moved here as it was. The one change is how a listing is picked: the page names it by its id,
/// and the trade is opened on this side's own copy of it.
/// </remarks>
public sealed class MarketLabDemo : IDisposable
{
    // The real marketplace service — in-memory, real logic, no infrastructure.
    private readonly InMemoryMarketService _market = new();

    // Two fixed demo identities so a trade has a seller and a buyer.
    private const string SellerUhid = "aether:themba:01";

    private readonly List<string> _log = new();

    public MarketLabDemo()
    {
        _market.ListingReceived += OnListing;

        // Seed a few neighbourhood listings so browsing and search have something to find.
        Task.Run(async () =>
        {
            await _market.CreateListingAsync(SellerUhid, "Second-hand bicycle",
                "Well-kept mountain bike, small frame", 850m, "ke7fqr", MarketCategory.Goods);
            await _market.CreateListingAsync("aether:lindiwe:03", "Weekend plumbing",
                "Blocked drains, taps and geysers — same-day", 450m, "ke7fqx", MarketCategory.Services);
            await _market.CreateListingAsync("aether:sbu:04", "Grade 10 maths tutoring",
                "Two afternoons a week, your place or mine", 200m, "ke7fpp", MarketCategory.Labour);

            await ShowAll();
        }).GetAwaiter().GetResult();
    }

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    public string BuyerUhid { get; } = "aether:you:02";

    // Post-a-listing form.
    public string Title { get; set; } = "Second-hand bicycle";
    public string Desc { get; set; } = "Well-kept mountain bike, small frame";
    public decimal Price { get; set; } = 850;
    public string GeoHash { get; set; } = "ke7fqr";
    public MarketCategory Category { get; set; } = MarketCategory.Goods;

    // Browse / search.
    public string Center { get; set; } = "ke7fqr";
    public int Radius { get; set; } = 2;
    public string Query { get; set; } = "";
    public string SearchCategory { get; set; } = "";

    public IReadOnlyList<MarketListing> Results { get; private set; } = Array.Empty<MarketListing>();
    public string ResultLabel { get; private set; } = "All listings";
    public MarketListing? Selected { get; private set; }
    public TradeEscrow? Escrow { get; private set; }
    public IReadOnlyList<string> Log => _log.ToList();

    public bool CanPost => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(GeoHash) && Price > 0;

    private void OnListing(object? _, MarketListing l) =>
        Say($"listing dropped: “{l.Title}” R{l.PriceZAR:0} @ {l.GeoHash} ({l.Category}) by {Petname(l.SellerUhid)}");

    private async Task ShowAll()
    {
        // An empty search query matches every (non-expired) listing — the honest "everything the
        // node holds", independent of any one geohash, so a freshly-posted listing always shows.
        Results = await _market.SearchAsync("", null);
        ResultLabel = $"All listings ({Results.Count})";
        Changed?.Invoke();
    }

    public async Task Post()
    {
        if (!CanPost) return;
        var listing = await _market.CreateListingAsync(SellerUhid, Title.Trim(), Desc.Trim(),
            Price, GeoHash.Trim(), Category);
        Title = ""; Desc = "";
        await ShowAll();
        Pick(listing);
    }

    public async Task BrowseNearby()
    {
        var center = Center.Trim();
        var radius = Math.Clamp(Radius, 1, 6);
        Results = await _market.BrowseNearbyAsync(center, radius);
        var prefixLen = Math.Max(1, center.Length - radius + 1);
        var prefix = center[..Math.Min(prefixLen, center.Length)];
        ResultLabel = $"Near {center} (±{radius}) — prefix “{prefix}”: {Results.Count} found";
        Say($"browse {center} ±{radius} → {Results.Count} listing(s) inside geohash prefix “{prefix}”");
    }

    public async Task Search()
    {
        MarketCategory? cat = string.IsNullOrEmpty(SearchCategory) ? null : Enum.Parse<MarketCategory>(SearchCategory);
        Results = await _market.SearchAsync(Query.Trim(), cat);
        ResultLabel = $"Search “{Query.Trim()}”{(cat is null ? "" : $" in {cat}")}: {Results.Count} found";
        Say($"search “{Query.Trim()}”{(cat is null ? "" : $" ({cat})")} → {Results.Count} match(es)");
    }

    /// <summary>Pick the listing with this id from what is showing.</summary>
    public void Select(Guid listingId)
    {
        if (Results.FirstOrDefault(l => l.ListingId == listingId) is { } listing)
        {
            Pick(listing);
        }
    }

    private void Pick(MarketListing listing)
    {
        Selected = listing;
        Escrow = null;
        Changed?.Invoke();
    }

    public async Task Initiate()
    {
        if (Selected is null) return;
        Escrow = await _market.InitiateTradeAsync(Selected, BuyerUhid);
        Say($"escrow {Short(Escrow.EscrowId)} opened for “{Selected.Title}” → {Escrow.State}");
    }

    public async Task BuyerConfirm()
    {
        if (Escrow is null) return;
        Escrow = await _market.ConfirmTradeAsync(Escrow, TradeRole.Buyer);
        Say($"buyer confirmed → {Escrow.State}");
    }

    public async Task SellerConfirm()
    {
        if (Escrow is null) return;
        Escrow = await _market.ConfirmTradeAsync(Escrow, TradeRole.Seller);
        Say($"seller confirmed → {Escrow.State}");
    }

    public async Task Dispute()
    {
        if (Escrow is null) return;
        await _market.DisputeAsync(Escrow, "item not as described");
        Say($"dispute raised → {Escrow.State}");
    }

    public void ResetTrade()
    {
        Escrow = null;
        Changed?.Invoke();
    }

    private void Say(string text)
    {
        _log.Add(text);
        if (_log.Count > 120) _log.RemoveRange(0, _log.Count - 120);
        Changed?.Invoke();
    }

    private static string Short(Guid id) => id.ToString()[..8];

    private static string Petname(string uhid)
    {
        var parts = uhid.Split(':');
        return parts.Length >= 2 && parts[1].Length > 0
            ? char.ToUpperInvariant(parts[1][0]) + parts[1][1..]
            : uhid;
    }

    public void Dispose() => _market.ListingReceived -= OnListing;
}

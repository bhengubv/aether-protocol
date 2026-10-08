// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class MarketLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public MarketLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewMarketLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetMarketLab, global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    public string BuyerUhid => Now.BuyerUhid;
    public bool CanPost => Now.CanPost;
    public global::AetherNet.Market.Models.MarketCategory Category
    {
        get => Now.Category;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabCategory, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public string Center
    {
        get => Now.Center;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabCenter, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public string Desc
    {
        get => Now.Desc;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabDesc, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public global::AetherNet.Market.Models.TradeEscrow? Escrow => Now.Escrow;
    public string GeoHash
    {
        get => Now.GeoHash;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabGeoHash, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public global::System.Collections.Generic.IReadOnlyList<string> Log => Now.Log;
    public decimal Price
    {
        get => Now.Price;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabPrice, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public string Query
    {
        get => Now.Query;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabQuery, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public int Radius
    {
        get => Now.Radius;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabRadius, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public string ResultLabel => Now.ResultLabel;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Market.Models.MarketListing> Results => Now.Results;
    public string SearchCategory
    {
        get => Now.SearchCategory;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabSearchCategory, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }
    public global::AetherNet.Market.Models.MarketListing? Selected => Now.Selected;
    public string Title
    {
        get => Now.Title;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetMarketLabTitle, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab); }
    }

    public async global::System.Threading.Tasks.Task BrowseNearby()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabBrowseNearby, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public async global::System.Threading.Tasks.Task BuyerConfirm()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabBuyerConfirm, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MarketLabDispose);
    }

    public async global::System.Threading.Tasks.Task Dispute()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabDispute, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public async global::System.Threading.Tasks.Task Initiate()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabInitiate, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public async global::System.Threading.Tasks.Task Post()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabPost, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public void ResetTrade()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MarketLabResetTrade, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public async global::System.Threading.Tasks.Task Search()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabSearch, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public void Select(global::System.Guid listingId)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MarketLabSelect, new { listingId }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public async global::System.Threading.Tasks.Task SellerConfirm()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MarketLabSellerConfirm, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventMarketLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string BuyerUhid { get; init; } = default!;
        public bool CanPost { get; init; }
        public global::AetherNet.Market.Models.MarketCategory Category { get; init; }
        public string Center { get; init; } = default!;
        public string Desc { get; init; } = default!;
        public global::AetherNet.Market.Models.TradeEscrow? Escrow { get; init; }
        public string GeoHash { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<string> Log { get; init; } = default!;
        public decimal Price { get; init; }
        public string Query { get; init; } = default!;
        public int Radius { get; init; }
        public string ResultLabel { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Market.Models.MarketListing> Results { get; init; } = default!;
        public string SearchCategory { get; init; } = default!;
        public global::AetherNet.Market.Models.MarketListing? Selected { get; init; }
        public string Title { get; init; } = default!;
    }
}

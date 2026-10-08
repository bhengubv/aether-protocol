// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public static class HandedCard
{
    public static async global::System.Threading.Tasks.Task SeedAsync(global::AetherNet.Browser.MeshWebService mesh, global::AetherNet.Browser.MyPages mine, global::AetherNet.Sample.Shared.Services.HandedCard.OpenPackaged open, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.HandedCardSeed, null, cancellationToken: cancellationToken);
    }

    /// <summary>Opens a file packaged with the app. The service seeds from its own; this is the page's way of asking.</summary>
    public delegate global::System.Threading.Tasks.Task<global::System.IO.Stream?> OpenPackaged(string named);
}

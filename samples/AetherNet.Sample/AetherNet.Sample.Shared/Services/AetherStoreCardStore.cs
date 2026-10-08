// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class AetherStoreCardStore
{
    public const string NameKey = "my_name";
    public const string OldCardKey = "my_card";
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public AetherStoreCardStore(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
    }
}

// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class PanicWipeService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public PanicWipeService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
    }

    public void Wipe() => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PanicWipeWipe, null);
}

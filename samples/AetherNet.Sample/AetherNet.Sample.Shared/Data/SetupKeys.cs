// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Data;

public static class SetupKeys
{
    public const string AetherNet = "setup.aethernet";
    public const string Complete = "setup.complete";
    public const string GatewayEnabled = "setup.gateway";
    public const string Theme = "appearance.theme";
}

// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public static class Handoff
{
    public static global::AetherNet.Sample.Shared.Services.Handoff.Note? Describe(string? route, string? draft = null, double? at = null) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Sample.Shared.Services.Handoff.Note?>(global::AetherNetNodeService.Ipc.NodeOp.HandoffDescribe, new { route, draft, at });

    public enum Kind
    {
        Unknown = 0,
        Card = 1,
        Chat = 2,
    }

    public record Note(int V, global::AetherNet.Sample.Shared.Services.Handoff.Kind Kind, string Target, string? Draft = null, double? At = null);
}

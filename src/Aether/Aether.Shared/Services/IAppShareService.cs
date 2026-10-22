// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace Aether.Shared.Services;

public interface IAppShareService
{
    bool IsSupported { get; }
    long SizeBytes { get; }
    string? UnavailableReason { get; }
}

/// <summary>The IAppShareService AetherNetService answers, through its menu.</summary>
public sealed class AppShareServiceFromService : IAppShareService
{
    private readonly global::Aether.Shared.Cache.ServiceMenu _menu;

    public AppShareServiceFromService(global::Aether.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetAppShare, global::AetherNetNodeService.Ipc.NodeOp.GetAppShare);
    public bool IsSupported => Now.IsSupported;
    public long SizeBytes => Now.SizeBytes;
    public string? UnavailableReason => Now.UnavailableReason;

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool IsSupported { get; init; }
        public long SizeBytes { get; init; }
        public string? UnavailableReason { get; init; }
    }
}

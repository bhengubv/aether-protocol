// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Cast;

public sealed class UpnpRendererService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public UpnpRendererService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    public void ReportEnded() => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.UpnpRendererReportEnded, null);

    public void ReportProgress(long positionMs, long durationMs) => _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.UpnpRendererReportProgress, new { positionMs, durationMs });

    public event global::System.Action? PauseRequested;
    public event global::System.Action<string, string>? PlayRequested;
    public event global::System.Action<long>? SeekRequested;
    public event global::System.Action? StopRequested;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventUpnpRendererPauseRequested: PauseRequested?.Invoke(); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventUpnpRendererPlayRequested:
            {
                var said = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<PlayRequestedSaid>(body);
                PlayRequested?.Invoke(said.Arg1, said.Arg2);
                break;
            }
            case global::AetherNetNodeService.Ipc.NodeOp.EventUpnpRendererSeekRequested: SeekRequested?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<long>(body)); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventUpnpRendererStopRequested: StopRequested?.Invoke(); break;
        }
    }

    private sealed record PlayRequestedSaid(string Arg1, string Arg2);
}

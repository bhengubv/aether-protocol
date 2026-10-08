// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class AttachmentService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public AttachmentService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    public async global::System.Threading.Tasks.Task<byte[]?> GetAsync(string hash, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<byte[]?>(global::AetherNetNodeService.Ipc.NodeOp.AttachmentGet, new { hash }, cancellationToken: cancellationToken);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<double> ProgressOfAsync(string hash, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<double>(global::AetherNetNodeService.Ipc.NodeOp.AttachmentProgressOf, new { hash }, cancellationToken: cancellationToken);
        return answer;
    }

    public event global::System.Action<string>? Arrived;
    public event global::System.Action<string, double>? Progress;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventAttachmentArrived: Arrived?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<string>(body)); break;
            case global::AetherNetNodeService.Ipc.NodeOp.EventAttachmentProgress:
            {
                var said = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<ProgressSaid>(body);
                Progress?.Invoke(said.Arg1, said.Arg2);
                break;
            }
        }
    }

    private sealed record ProgressSaid(string Arg1, double Arg2);
}

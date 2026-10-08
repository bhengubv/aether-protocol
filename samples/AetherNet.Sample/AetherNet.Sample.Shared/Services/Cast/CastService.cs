// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Cast;

public sealed class CastService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public CastService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
    }

    public async global::System.Threading.Tasks.Task<bool> CastAsync(global::AetherNet.Sample.Shared.Services.Cast.CastTarget target, string hash, string contentType, string title, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastCast, new { target, hash, contentType, title }, cancellationToken: cancellationToken);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Cast.CastTarget>> FindTargetsAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Cast.CastTarget>>(global::AetherNetNodeService.Ipc.NodeOp.CastFindTargets, null, cancellationToken: cancellationToken);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> PauseAsync(global::AetherNet.Sample.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastPause, new { t });
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> PlayAsync(global::AetherNet.Sample.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastPlay, new { t });
        return answer;
    }

    public async global::System.Threading.Tasks.Task<global::AetherNet.Sample.Shared.Services.Cast.CastStatus?> StatusAsync(global::AetherNet.Sample.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<global::AetherNet.Sample.Shared.Services.Cast.CastStatus?>(global::AetherNetNodeService.Ipc.NodeOp.CastStatus, new { t });
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> StopAsync(global::AetherNet.Sample.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastStop, new { t });
        return answer;
    }
}

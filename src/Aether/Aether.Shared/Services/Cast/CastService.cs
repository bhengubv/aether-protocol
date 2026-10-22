// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace Aether.Shared.Services.Cast;

public sealed class CastService
{
    private readonly global::Aether.Shared.Cache.ServiceMenu _menu;

    public CastService(global::Aether.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
    }

    public async global::System.Threading.Tasks.Task<bool> CastAsync(global::Aether.Shared.Services.Cast.CastTarget target, string hash, string contentType, string title, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastCast, new { target, hash, contentType, title }, cancellationToken: cancellationToken);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::Aether.Shared.Services.Cast.CastTarget>> FindTargetsAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<global::System.Collections.Generic.IReadOnlyList<global::Aether.Shared.Services.Cast.CastTarget>>(global::AetherNetNodeService.Ipc.NodeOp.CastFindTargets, null, cancellationToken: cancellationToken);
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> PauseAsync(global::Aether.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastPause, new { t });
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> PlayAsync(global::Aether.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastPlay, new { t });
        return answer;
    }

    public async global::System.Threading.Tasks.Task<global::Aether.Shared.Services.Cast.CastStatus?> StatusAsync(global::Aether.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<global::Aether.Shared.Services.Cast.CastStatus?>(global::AetherNetNodeService.Ipc.NodeOp.CastStatus, new { t });
        return answer;
    }

    public async global::System.Threading.Tasks.Task<bool> StopAsync(global::Aether.Shared.Services.Cast.CastTarget t)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.CastStop, new { t });
        return answer;
    }
}

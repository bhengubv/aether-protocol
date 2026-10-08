// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public sealed class QuietHelpService
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    public QuietHelpService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
    {
        _menu = menu;
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetQuietHelp, global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    public bool Available => Now.Available;
    public global::AetherNetNodeService.HelpState Mine => Now.Mine;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpWatchCase> Watching => Now.Watching;

    public void Listen()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpListen, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    }

    public async global::System.Threading.Tasks.Task MarkSafeAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpMarkSafe, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    }

    public async global::System.Threading.Tasks.Task RefreshAsync(global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpRefresh, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    }

    public async global::System.Threading.Tasks.Task SetGuardiansAsync(global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpGuardian> guardians, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpSetGuardians, new { guardians }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    }

    public async global::System.Threading.Tasks.Task SetOptionsAsync(global::AetherNetNodeService.HelpTriggers triggers, global::AetherNetNodeService.HelpAdvertForm advert, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpSetOptions, new { triggers, advert }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    }

    public async global::System.Threading.Tasks.Task SetTriggerAsync(global::AetherNetNodeService.HelpTrigger trigger, bool on, global::System.Threading.CancellationToken cancellationToken = default)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpSetTrigger, new { trigger, on }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
    }

    public async global::System.Threading.Tasks.Task<bool> StartAsync(global::AetherNetNodeService.HelpKind kind, global::System.Threading.CancellationToken cancellationToken = default)
    {
        var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.QuietHelpStart, new { kind }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp);
        return answer;
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventQuietHelp: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool Available { get; init; }
        public global::AetherNetNodeService.HelpState Mine { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpWatchCase> Watching { get; init; } = default!;
    }
}

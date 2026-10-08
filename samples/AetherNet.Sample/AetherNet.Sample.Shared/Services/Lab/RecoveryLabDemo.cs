// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class RecoveryLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public RecoveryLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewRecoveryLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetRecoveryLab, global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab);
    public string Entered
    {
        get => Now.Entered;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetRecoveryLabEntered, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab); }
    }
    public bool Match => Now.Match;
    public string? Result => Now.Result;
    public string TagA => Now.TagA;
    public string? TagB => Now.TagB;
    public bool Valid => Now.Valid;
    public global::System.Collections.Generic.IReadOnlyList<string> Words => Now.Words;

    public void CorruptOne()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.RecoveryLabCorruptOne, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab);
    }

    public void Generate()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.RecoveryLabGenerate, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab);
    }

    public void Recover()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.RecoveryLabRecover, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab);
    }

    public void ResetPhrase()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.RecoveryLabResetPhrase, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventRecoveryLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string Entered { get; init; } = default!;
        public bool Match { get; init; }
        public string? Result { get; init; }
        public string TagA { get; init; } = default!;
        public string? TagB { get; init; }
        public bool Valid { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<string> Words { get; init; } = default!;
    }
}

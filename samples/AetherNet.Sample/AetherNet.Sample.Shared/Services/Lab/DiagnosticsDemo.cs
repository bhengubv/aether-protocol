// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class DiagnosticsDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public DiagnosticsDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewDiagnostics, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetDiagnostics, global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics);
    public string ChallengeIn
    {
        get => Now.ChallengeIn;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetDiagnosticsChallengeIn, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics); }
    }
    public string ChallengeOut => Now.ChallengeOut;
    public string ExpectTag
    {
        get => Now.ExpectTag;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetDiagnosticsExpectTag, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics); }
    }
    public string IStart => Now.IStart;
    public string PeerTag
    {
        get => Now.PeerTag;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetDiagnosticsPeerTag, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics); }
    }
    public string ProofIn
    {
        get => Now.ProofIn;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetDiagnosticsProofIn, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics); }
    }
    public string ProofOut => Now.ProofOut;
    public string Rv => Now.Rv;
    public string VerifyOut => Now.VerifyOut;

    public void Derive()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DiagnosticsDerive, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics);
    }

    public void Issue()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DiagnosticsIssue, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics);
    }

    public async global::System.Threading.Tasks.Task Prove()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.DiagnosticsProve, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics);
    }

    public void Verify()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.DiagnosticsVerify, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventDiagnostics: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string ChallengeIn { get; init; } = default!;
        public string ChallengeOut { get; init; } = default!;
        public string ExpectTag { get; init; } = default!;
        public string IStart { get; init; } = default!;
        public string PeerTag { get; init; } = default!;
        public string ProofIn { get; init; } = default!;
        public string ProofOut { get; init; } = default!;
        public string Rv { get; init; } = default!;
        public string VerifyOut { get; init; } = default!;
    }
}

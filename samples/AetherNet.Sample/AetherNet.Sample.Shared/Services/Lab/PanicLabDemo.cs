// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class PanicLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public PanicLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewPanicLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetPanicLab, global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab);
    public bool Armed => Now.Armed;
    public string DuressPin
    {
        get => Now.DuressPin;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetPanicLabDuressPin, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab); }
    }
    public string EraseAfter => Now.EraseAfter;
    public string EraseBefore => Now.EraseBefore;
    public string ErasedName => Now.ErasedName;
    public string ExampleNames => Now.ExampleNames;
    public string HashHex => Now.HashHex;
    public int IdentityKeyCount => Now.IdentityKeyCount;
    public int ManifestCount => Now.ManifestCount;
    public int MaxPreKeys => Now.MaxPreKeys;
    public string? Outcome => Now.Outcome;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PanicLabDemo.EntryView> Store => Now.Store;
    public string UnlockPin
    {
        get => Now.UnlockPin;
        set { _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.SetPanicLabUnlockPin, new { value }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab); }
    }
    public bool Wiped => Now.Wiped;

    public void Arm()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PanicLabArm, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab);
    }

    public void Reset()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PanicLabReset, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab);
    }

    public void Unlock()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PanicLabUnlock, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventPanicLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public bool Armed { get; init; }
        public string DuressPin { get; init; } = default!;
        public string EraseAfter { get; init; } = default!;
        public string EraseBefore { get; init; } = default!;
        public string ErasedName { get; init; } = default!;
        public string ExampleNames { get; init; } = default!;
        public string HashHex { get; init; } = default!;
        public int IdentityKeyCount { get; init; }
        public int ManifestCount { get; init; }
        public int MaxPreKeys { get; init; }
        public string? Outcome { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PanicLabDemo.EntryView> Store { get; init; } = default!;
        public string UnlockPin { get; init; } = default!;
        public bool Wiped { get; init; }
    }

    public record EntryView(string Name, bool Alive, string Preview);
}

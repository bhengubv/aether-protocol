// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class PoLLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public PoLLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewPoLLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetPoLLab, global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab);
    public string EncounterGeohash => Now.EncounterGeohash;
    public string EncounterPlace => Now.EncounterPlace;
    public double MinWeight => Now.MinWeight;
    public int MinWitnesses => Now.MinWitnesses;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PoLLabDemo.WitnessRow> Rows => Now.Rows;
    public bool SelfVouch => Now.SelfVouch;
    public string SignableBodyHex => Now.SignableBodyHex;
    public long TimeBucket => Now.TimeBucket;
    public global::AetherNet.Cartography.PoLVerdict Verdict => Now.Verdict;

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PoLLabDemo.LogLine> Log()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PoLLabDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.PoLLabLog, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab);
        return answer;
    }

    public void SetSelfVouch(bool on)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PoLLabSetSelfVouch, new { on }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab);
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PoLLabStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab);
    }

    public void Toggle(string name)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PoLLabToggle, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab);
    }

    public void ToggleTamper(string name)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.PoLLabToggleTamper, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventPoLLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public string EncounterGeohash { get; init; } = default!;
        public string EncounterPlace { get; init; } = default!;
        public double MinWeight { get; init; }
        public int MinWitnesses { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.PoLLabDemo.WitnessRow> Rows { get; init; } = default!;
        public bool SelfVouch { get; init; }
        public string SignableBodyHex { get; init; } = default!;
        public long TimeBucket { get; init; }
        public global::AetherNet.Cartography.PoLVerdict Verdict { get; init; } = default!;
    }

    public record LogLine(string Text);

    public record WitnessRow(string Name, double Weight, bool Known, bool Collected, bool Tampered, bool? Verified, bool Counted, string Badge, string Note);
}

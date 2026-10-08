// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class VicinityLabDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public VicinityLabDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewVicinityLab, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetVicinityLab, global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
    public global::AetherNet.Market.Models.PoVScore DefenceScore => Now.DefenceScore;
    public string DefenceSubjectPetname => Now.DefenceSubjectPetname;
    public bool HasSampleToken => Now.HasSampleToken;
    public global::AetherNet.Market.Models.PoVToken? LastAcceptedOnMesh => Now.LastAcceptedOnMesh;
    public global::AetherNet.Market.Models.PoVScore MeshScore => Now.MeshScore;
    public bool? PristineVerify => Now.PristineVerify;
    public string SubjectUhid => Now.SubjectUhid;
    public bool? TamperedVerify => Now.TamperedVerify;
    public int WitnessesRemaining => Now.WitnessesRemaining;

    public async global::System.Threading.Tasks.Task AddVouchAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabAddVouch, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
    }

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabDispose);
    }

    public async global::System.Threading.Tasks.Task ReportDefectionAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabReportDefection, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.VicinityLabDemo.LogLine> Snapshot()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.VicinityLabDemo.LogLine>>(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
        return answer;
    }

    public void Start()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabStart, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
    }

    public async global::System.Threading.Tasks.Task TamperAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabTamper, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
    }

    public async global::System.Threading.Tasks.Task VouchOverMeshAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.VicinityLabVouchOverMesh, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventVicinityLab: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Market.Models.PoVScore DefenceScore { get; init; } = default!;
        public string DefenceSubjectPetname { get; init; } = default!;
        public bool HasSampleToken { get; init; }
        public global::AetherNet.Market.Models.PoVToken? LastAcceptedOnMesh { get; init; }
        public global::AetherNet.Market.Models.PoVScore MeshScore { get; init; } = default!;
        public bool? PristineVerify { get; init; }
        public string SubjectUhid { get; init; } = default!;
        public bool? TamperedVerify { get; init; }
        public int WitnessesRemaining { get; init; }
    }

    public record LogLine(string Text, bool Emphasis);
}

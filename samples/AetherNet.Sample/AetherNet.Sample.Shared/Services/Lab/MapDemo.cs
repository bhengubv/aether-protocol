// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class MapDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public MapDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewMap, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetMap, global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    public global::AetherNet.Sample.Shared.Services.Lab.MapDemo.HoursConflictView? HoursConflict => Now.HoursConflict;
    public bool LeratoEdited => Now.LeratoEdited;
    public global::System.Collections.Generic.IReadOnlyList<string> Log => Now.Log;
    public bool Merged => Now.Merged;
    public global::AetherNet.Sample.Shared.Services.Lab.MapDemo.ObservedView Observed => Now.Observed;
    public string OwnerKeyShort => Now.OwnerKeyShort;
    public bool ThaboEdited => Now.ThaboEdited;

    public void ConfirmRamp()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapConfirmRamp, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public void Downvote()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapDownvote, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public void EditAsLerato()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapEditAsLerato, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public void EditAsThabo()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapEditAsThabo, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public void Merge()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapMerge, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public async global::System.Threading.Tasks.Task MoveQueryAsync(int row, int col)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MapMoveQuery, new { row, col }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.MapDemo.CellView> ProximityCells()
    {
        var answer = _menu.Call<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.MapDemo.CellView>>(global::AetherNetNodeService.Ipc.NodeOp.MapProximityCells, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
        return answer;
    }

    public void ReconfirmRamp()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapReconfirmRamp, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public global::AetherNet.Sample.Shared.Services.Lab.MapDemo.FeatureView Replica(bool thabo)
    {
        var answer = _menu.Call<global::AetherNet.Sample.Shared.Services.Lab.MapDemo.FeatureView>(global::AetherNetNodeService.Ipc.NodeOp.MapReplica, new { thabo }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
        return answer;
    }

    public void Reset()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapReset, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    public bool TryImpostorMerge()
    {
        var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.MapTryImpostorMerge, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
        return answer;
    }

    public void Upvote()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MapUpvote, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMap);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventMap: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Sample.Shared.Services.Lab.MapDemo.HoursConflictView? HoursConflict { get; init; }
        public bool LeratoEdited { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<string> Log { get; init; } = default!;
        public bool Merged { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.MapDemo.ObservedView Observed { get; init; } = default!;
        public string OwnerKeyShort { get; init; } = default!;
        public bool ThaboEdited { get; init; }
    }

    public record AttrView(string Key, string Value, string Clock);

    public record CellView(int Row, int Col, string Cell, string? Poi, bool InRing, bool Matched, bool IsCenter);

    public record FeatureView(string LocationCell, global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.MapDemo.AttrView> Attributes, global::System.Collections.Generic.IReadOnlyList<string> Tags);

    public record HoursConflictView(string ValueT, string ClockT, string ValueL, string ClockL, bool WinnerIsLerato, bool Merged);

    public record ObservedView(bool RampPresent, int Witnesses, int Threshold, bool Confirmed, long Sentiment, global::System.Collections.Generic.IReadOnlyList<string> Attestors);
}

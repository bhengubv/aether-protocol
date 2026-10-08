// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class ForgeDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public ForgeDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewForge, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetForge, global::AetherNetNodeService.Ipc.NodeOp.EventForge);
    public global::System.Collections.Generic.IReadOnlyCollection<string> LearnedByGossip => Now.LearnedByGossip;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.ForgeDemo.LogLine> Log => Now.Log;
    public global::AetherNet.Forge.Models.ForgeStats? StatsA => Now.StatsA;
    public global::AetherNet.Forge.Models.ForgeStats? StatsB => Now.StatsB;

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.ForgeDispose);
    }

    public async global::System.Threading.Tasks.Task FetchOnBAsync(string packageId, int times)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ForgeFetchOnB, new { packageId, times }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventForge);
    }

    public void Init()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.ForgeInit, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventForge);
    }

    public async global::System.Threading.Tasks.Task<global::AetherNet.Forge.Models.ForgeEntry?> LookupOnBAsync(string packageId)
    {
        var answer = await _menu.CallAsync<global::AetherNet.Forge.Models.ForgeEntry?>(global::AetherNetNodeService.Ipc.NodeOp.ForgeLookupOnB, new { packageId }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventForge);
        return answer;
    }

    public async global::System.Threading.Tasks.Task PublishOnAAsync(string packageId, int sizeKb)
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ForgePublishOnA, new { packageId, sizeKb }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventForge);
    }

    public async global::System.Threading.Tasks.Task SeedSampleAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.ForgeSeedSample, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventForge);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventForge: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.IReadOnlyCollection<string> LearnedByGossip { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.ForgeDemo.LogLine> Log { get; init; } = default!;
        public global::AetherNet.Forge.Models.ForgeStats? StatsA { get; init; }
        public global::AetherNet.Forge.Models.ForgeStats? StatsB { get; init; }
    }

    public record LogLine(string Who, string Text);
}

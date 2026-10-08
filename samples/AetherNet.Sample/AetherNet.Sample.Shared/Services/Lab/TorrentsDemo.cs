// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class TorrentsDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public TorrentsDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewTorrents, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetTorrents, global::AetherNetNodeService.Ipc.NodeOp.EventTorrents);
    public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.TorrentBuild? Built => Now.Built;
    public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.DhtView? Dht => Now.Dht;
    public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.TorrentExport? Export => Now.Export;
    public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.MeshIngest? Ingest => Now.Ingest;
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.LogLine> Log => Now.Log;

    public void BuildTorrent(string name, string text, string? tracker)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.TorrentsBuildTorrent, new { name, text, tracker }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTorrents);
    }

    public async global::System.Threading.Tasks.Task IngestIntoMeshAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.TorrentsIngestIntoMesh, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTorrents);
    }

    public void ReSeedAsTorrent(string contentName, string body)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.TorrentsReSeedAsTorrent, new { contentName, body }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTorrents);
    }

    public async global::System.Threading.Tasks.Task RunLiveDhtAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.TorrentsRunLiveDht, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTorrents);
    }

    public void ShowRoutingTable()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.TorrentsShowRoutingTable, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventTorrents);
    }

    /// <summary>Stop listening for what the demo says changed.</summary>
    public void Dispose() => _menu.Told -= OnTold;

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventTorrents: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.TorrentBuild? Built { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.DhtView? Dht { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.TorrentExport? Export { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.MeshIngest? Ingest { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.LogLine> Log { get; init; } = default!;
    }

    public record DhtView(string SelfId, int Contacts, string Target, global::AetherNet.Sample.Shared.Services.Lab.TorrentsDemo.RoutingRow[] Closest);

    public record LogLine(string Who, string Text);

    public record MeshIngest(string Root, int ChunkCount, int ChunkSize, long TotalBytes, bool Verified, string InfoHashV1, string MeshRootOverFile);

    public record RoutingRow(string NodeId, string Distance, string EndPoint);

    public record TorrentBuild(string Name, int SourceBytes, int TorrentBytes, string InfoHashV1, string Magnet, long PieceLength, int PieceCount, string FirstPiece, string MerkleRootV2, string InfoHashV2);

    public record TorrentExport(string Name, int SourceBytes, int TorrentBytes, string InfoHashV1, int PieceCount, string Announce);
}

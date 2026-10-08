// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services.Lab;

public sealed class FilesDemo : global::System.IDisposable
{
    private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

    /// <summary>A fresh one, made in AetherNetService as this page asked.</summary>
    public FilesDemo()
    {
        _menu = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.NewFiles, null);
        _menu.Told += OnTold;
    }

    private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetFiles, global::AetherNetNodeService.Ipc.NodeOp.EventFiles);
    public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.LogLine> Log => Now.Log;
    public global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.PublishView? Published => Now.Published;
    public global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.DownloadReport? Report => Now.Report;
    public bool Running => Now.Running;
    public global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.ShuffleView? Shuffle => Now.Shuffle;

    public void Dispose()
    {
        _menu.Told -= OnTold;
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.FilesDispose);
    }

    public bool[] HaveSnapshot()
    {
        var answer = _menu.Call<bool[]>(global::AetherNetNodeService.Ipc.NodeOp.FilesHaveSnapshot, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFiles);
        return answer;
    }

    public string HolderOf(int chunkIndex)
    {
        var answer = _menu.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.FilesHolderOf, new { chunkIndex }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFiles);
        return answer;
    }

    public void Publish(int sizeKb)
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.FilesPublish, new { sizeKb }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFiles);
    }

    public void RunChunkShuffle()
    {
        _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.FilesRunChunkShuffle, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFiles);
    }

    public async global::System.Threading.Tasks.Task RunDownloadAsync()
    {
        await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.FilesRunDownload, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventFiles);
    }

    public event global::System.Action? Changed;

    private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
    {
        switch (op)
        {
            case global::AetherNetNodeService.Ipc.NodeOp.EventFiles: Changed?.Invoke(); break;
        }
    }

    // What the service last said this holds.
    private sealed class Shown
    {
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.LogLine> Log { get; init; } = default!;
        public global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.PublishView? Published { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.DownloadReport? Report { get; init; }
        public bool Running { get; init; }
        public global::AetherNet.Sample.Shared.Services.Lab.FilesDemo.ShuffleView? Shuffle { get; init; }
    }

    public record DownloadReport(int ChunksFetched, int ChunksResumed, int Retries, int SegmentSteals, int MaxParallelism, int FromSeederA, int FromSeederB, bool Verified, long ElapsedMs);

    public record LogLine(string Who, string Text);

    public record PublishView(string Name, long TotalBytes, int ChunkSize, int ChunkCount, string Root, string FirstChunkHash, bool SelfVerifies);

    public record ShuffleView(int[] AssignedA, int[] AssignedB, bool Disjoint);
}

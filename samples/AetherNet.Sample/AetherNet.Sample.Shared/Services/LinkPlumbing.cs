// The app's side of AetherNetService's menu (NodeOp), made from the service's own signatures. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.

namespace AetherNet.Sample.Shared.Services;

public record MeshLink(global::System.Net.Sockets.TcpClient Client, global::System.Net.Sockets.NetworkStream Stream)
{
    public const int BulkDepth = 64;
    public const int VideoDepth = 6;
    public global::System.Collections.Concurrent.ConcurrentQueue<byte[]>[] Lanes { get; init; } = default!;
    public global::System.Threading.SemaphoreSlim Ready { get; init; } = default!;
}

// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Content.Download.Tests;

/// <summary>An <see cref="IContentService"/> whose chunk requests immediately store the chunk and
/// raise a verified <see cref="IContentService.ChunkReceived"/> — enough to drive <c>MeshChunkSource</c>.</summary>
internal sealed class FakeContentService : IContentService
{
    private readonly IContentStore _store;
    private readonly byte[] _file;
    private readonly int _chunkSize;

    public FakeContentService(IContentStore store, byte[] file, int chunkSize)
    {
        _store = store;
        _file = file;
        _chunkSize = chunkSize;
    }

    public event EventHandler<ContentDescriptor>? ContentAnnounced;
    public event EventHandler<ChunkArrivedEventArgs>? ChunkReceived;
    public event EventHandler<ContentDescriptor>? ContentComplete;

    public Task<ContentDescriptor> PublishAsync(string name, byte[] data, string contentType = "application/octet-stream", int chunkSizeBytes = 0, CancellationToken cancellationToken = default)
        => Task.FromResult(ContentDescriptor.FromBytes(name, data, contentType, chunkSizeBytes));

    public Task AnnounceAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task BroadcastBitmapAsync(string rootHash, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task RequestChunksAsync(string rootHash, IReadOnlyList<int> chunkIndices, string? peerUhid = null, CancellationToken cancellationToken = default)
    {
        foreach (var i in chunkIndices)
        {
            int start = i * _chunkSize;
            int len = Math.Min(_chunkSize, _file.Length - start);
            var bytes = new byte[len];
            Array.Copy(_file, start, bytes, 0, len);
            await _store.SaveChunkAsync(rootHash, i, bytes, cancellationToken).ConfigureAwait(false);
            ChunkReceived?.Invoke(this, new ChunkArrivedEventArgs
            {
                RootHash = rootHash,
                ChunkIndex = i,
                Verified = true,
                ContentComplete = false,
            });
        }
        _ = ContentAnnounced; _ = ContentComplete; // referenced to satisfy the compiler
    }

    public Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<byte[]?> AssembleAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        var d = await _store.GetDescriptorAsync(rootHash, cancellationToken).ConfigureAwait(false);
        if (d is null) return null;
        var outp = new byte[d.TotalBytes];
        long off = 0;
        for (int i = 0; i < d.ChunkCount; i++)
        {
            var b = await _store.GetChunkAsync(rootHash, i, cancellationToken).ConfigureAwait(false);
            if (b is null) return null;
            Array.Copy(b, 0, outp, off, b.Length);
            off += b.Length;
        }
        return outp;
    }
}

internal sealed class NoopMeshSender : IMeshSender
{
    public string LocalUhid => "content-node";
    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

internal sealed class NoopRoutingService : IRoutingService
{
    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default) => Task.FromResult<RouteEntry?>(null);
    public RouteEntry? GetCachedRoute(string destinationUhid) => null;
    public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();
    public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Cards.Tests;

/// <summary>Captures every packet a service tries to send, so a test can inspect or replay it.</summary>
internal sealed class CapturingMeshSender : IMeshSender
{
    public CapturingMeshSender(string uhid) => LocalUhid = uhid;

    public string LocalUhid { get; }
    public List<MeshPacket> Broadcasts { get; } = new();
    public List<(MeshPacket Packet, string NextHop)> Unicasts { get; } = new();

    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        Broadcasts.Add(packet);
        return Task.FromResult(1);
    }

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        Unicasts.Add((packet, nextHopUhid));
        return Task.FromResult(true);
    }
}

/// <summary>
/// In-memory <see cref="IContentService"/>: content-addresses a blob via
/// <see cref="ContentDescriptor.FromBytes"/> and stores the bytes by root hash so a test can assemble
/// them back. Distribution over the wire is out of scope here (that is IContentService's own domain).
/// </summary>
internal sealed class FakeContentService : IContentService
{
#pragma warning disable CS0067 // interface events are unused by this in-memory fake
    public event EventHandler<ContentDescriptor>? ContentAnnounced;
    public event EventHandler<ChunkArrivedEventArgs>? ChunkReceived;
    public event EventHandler<ContentDescriptor>? ContentComplete;
#pragma warning restore CS0067

    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public Task<ContentDescriptor> PublishAsync(string name, byte[] data, string contentType = "application/octet-stream", int chunkSizeBytes = 0, CancellationToken cancellationToken = default)
    {
        var descriptor = ContentDescriptor.FromBytes(name, data, contentType, chunkSizeBytes);
        _store[descriptor.RootHash] = (byte[])data.Clone();
        return Task.FromResult(descriptor);
    }

    public Task AnnounceAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task BroadcastBitmapAsync(string rootHash, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RequestChunksAsync(string rootHash, IReadOnlyList<int> chunkIndices, string? peerUhid = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<byte[]?> AssembleAsync(string rootHash, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryGetValue(rootHash, out var bytes) ? bytes : null);
}

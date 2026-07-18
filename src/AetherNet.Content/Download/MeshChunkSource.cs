// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Content.Download;

/// <summary>
/// An <see cref="IChunkSource"/> backed by the live mesh <see cref="IContentService"/>: it issues a
/// targeted chunk request and awaits the matching verified <see cref="IContentService.ChunkReceived"/>
/// event (the service verifies each arrival against the descriptor and stores it), then returns the
/// stored bytes. A per-fetch timeout surfaces as a transient failure so the downloader retries,
/// rotating to another peer via the broadcast request.
/// </summary>
public sealed class MeshChunkSource : IChunkSource, IDisposable
{
    private readonly IContentService _content;
    private readonly IContentStore _store;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<(string Root, int Index), TaskCompletionSource> _waiters = new();

    public MeshChunkSource(IContentService content, IContentStore store, TimeSpan? timeout = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _content.ChunkReceived += OnChunkReceived;
    }

    private void OnChunkReceived(object? sender, ChunkArrivedEventArgs e)
    {
        if (!e.Verified) return;
        if (_waiters.TryGetValue((e.RootHash, e.ChunkIndex), out var tcs))
            tcs.TrySetResult();
    }

    public async Task<byte[]> FetchChunkAsync(string rootHash, int chunkIndex, string? preferredPeer, CancellationToken cancellationToken)
    {
        // Already present (e.g. delivered by an unrelated broadcast)? Return immediately.
        var existing = await _store.GetChunkAsync(rootHash, chunkIndex, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;

        var tcs = _waiters.GetOrAdd((rootHash, chunkIndex),
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await _content.RequestChunksAsync(rootHash, new[] { chunkIndex }, preferredPeer, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ChunkSourceException($"timed out waiting for chunk {chunkIndex} of {rootHash}", permanent: false);
            }
        }
        finally
        {
            _waiters.TryRemove((rootHash, chunkIndex), out _);
        }

        var bytes = await _store.GetChunkAsync(rootHash, chunkIndex, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            throw new ChunkSourceException($"chunk {chunkIndex} of {rootHash} signalled complete but is absent from the store", permanent: false);
        return bytes;
    }

    public void Dispose() => _content.ChunkReceived -= OnChunkReceived;
}

// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.PeerWire;
using AetherNet.BitTorrent.Storage;

namespace AetherNet.BitTorrent.Client;

/// <summary>
/// Drives one peer connection for a single torrent: exchanges the handshake, advertises what we hold,
/// downloads the pieces we lack (rarest-first, verifying each against its SHA-1), and serves blocks a
/// peer requests. Symmetric — the same session both leeches and seeds — so a fully-populated
/// <see cref="PieceStore"/> acts as a seeder and an empty one as a leecher.
/// </summary>
public sealed class PeerSession
{
    private const int BlockSize = 16384; // 16 KiB — the standard BitTorrent block size

    private readonly PeerConnection _conn;
    private readonly PieceStore _store;
    private readonly byte[] _infoHash;
    private readonly byte[] _peerId;
    private readonly bool _initiator;
    private readonly RarestFirstPicker _picker;

    private Bitfield? _peerBitfield;

    // The piece we're currently downloading (-1 when idle).
    private int _currentPiece = -1;
    private byte[]? _pieceBuffer;
    private int _blocksOutstanding;

    /// <summary>Completes the first time our store becomes fully downloaded.</summary>
    public TaskCompletionSource DownloadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PeerSession(PeerConnection conn, PieceStore store, byte[] infoHash, bool initiator, byte[] peerId)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _infoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        _peerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
        _initiator = initiator;
        _picker = new RarestFirstPicker(store.PieceCount);
        for (int i = 0; i < store.PieceCount; i++)
            if (store.Has(i)) _picker.SetHave(i);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            var ours = new Handshake(_infoHash, _peerId, Handshake.DefaultReserved());
            await _conn.HandshakeAsync(ours, _initiator, ct).ConfigureAwait(false);
            await _conn.SendAsync(PeerMessage.Bitfield(_store.BuildBitfield().ToBytes()), ct).ConfigureAwait(false);

            if (_store.IsComplete) DownloadCompleted.TrySetResult();

            while (!ct.IsCancellationRequested)
            {
                var message = await _conn.ReceiveAsync(ct).ConfigureAwait(false);
                await HandleAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (EndOfStreamException) { /* peer closed the connection */ }
        catch (IOException) { /* socket torn down */ }
    }

    private async Task HandleAsync(PeerMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case PeerMessageType.Bitfield:
                _peerBitfield = new Bitfield(message.GetBitfield(), _store.PieceCount);
                _picker.AddPeer(_peerBitfield);
                await SendInterestedIfUsefulAsync(ct).ConfigureAwait(false);
                break;

            case PeerMessageType.Have:
                int have = message.GetHavePieceIndex();
                _peerBitfield ??= new Bitfield(_store.PieceCount);
                if ((uint)have < (uint)_store.PieceCount) _peerBitfield[have] = true;
                _picker.PeerHas(have);
                await SendInterestedIfUsefulAsync(ct).ConfigureAwait(false);
                break;

            case PeerMessageType.Unchoke:
                await PumpAsync(ct).ConfigureAwait(false);
                break;

            case PeerMessageType.Interested:
                if (_conn.AmChoking) await _conn.SendAsync(PeerMessage.Unchoke(), ct).ConfigureAwait(false);
                break;

            case PeerMessageType.Request:
                await ServeAsync(message, ct).ConfigureAwait(false);
                break;

            case PeerMessageType.Piece:
                await OnPieceAsync(message, ct).ConfigureAwait(false);
                break;

            // Choke / NotInterested / Port / keep-alive: no action needed here.
        }
    }

    private async Task SendInterestedIfUsefulAsync(CancellationToken ct)
    {
        if (_conn.AmInterested || _peerBitfield is null) return;
        for (int i = 0; i < _store.PieceCount; i++)
        {
            if (_peerBitfield[i] && !_store.Has(i))
            {
                await _conn.SendAsync(PeerMessage.Interested(), ct).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        if (_conn.PeerChoking || _peerBitfield is null || _currentPiece >= 0) return;

        int? pick = _picker.PickFor(_peerBitfield);
        if (pick is null) return;

        _currentPiece = pick.Value;
        int pieceLen = _store.LengthOfPiece(_currentPiece);
        _pieceBuffer = new byte[pieceLen];
        _blocksOutstanding = 0;
        for (int begin = 0; begin < pieceLen; begin += BlockSize)
        {
            int blockLen = Math.Min(BlockSize, pieceLen - begin);
            await _conn.SendAsync(PeerMessage.Request(_currentPiece, begin, blockLen), ct).ConfigureAwait(false);
            _blocksOutstanding++;
        }
    }

    private async Task OnPieceAsync(PeerMessage message, CancellationToken ct)
    {
        var (index, begin, block) = message.GetPiece();
        if (index != _currentPiece || _pieceBuffer is null) return; // stale / unexpected block
        if (begin < 0 || begin + block.Length > _pieceBuffer.Length) return;

        block.CopyTo(_pieceBuffer.AsSpan(begin));
        _blocksOutstanding--;
        if (_blocksOutstanding > 0) return;

        if (_store.TryComplete(_currentPiece, _pieceBuffer))
        {
            _picker.SetHave(_currentPiece);
            await _conn.SendAsync(PeerMessage.Have(_currentPiece), ct).ConfigureAwait(false);
        }
        else
        {
            _picker.Release(_currentPiece); // hash mismatch — let it be re-picked
        }

        _currentPiece = -1;
        _pieceBuffer = null;

        if (_store.IsComplete)
        {
            DownloadCompleted.TrySetResult();
            return;
        }
        await PumpAsync(ct).ConfigureAwait(false);
    }

    private async Task ServeAsync(PeerMessage message, CancellationToken ct)
    {
        if (_conn.AmChoking) return;
        var (index, begin, length) = message.GetBlockRef();
        if ((uint)index >= (uint)_store.PieceCount || !_store.Has(index)) return;
        if (length is <= 0 or > BlockSize) return; // ignore abusive block sizes
        if (begin < 0 || begin + length > _store.LengthOfPiece(index)) return;

        var block = _store.ReadBlock(index, begin, length);
        await _conn.SendAsync(PeerMessage.Piece(index, begin, block), ct).ConfigureAwait(false);
    }
}

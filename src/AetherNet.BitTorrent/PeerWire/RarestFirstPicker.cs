// SPDX-License-Identifier: MIT

namespace AetherNet.BitTorrent.PeerWire;

/// <summary>
/// Rarest-first piece selection: among the pieces a peer offers that we still need and that aren't
/// already being fetched, choose the one held by the fewest peers — which keeps the swarm healthy by
/// spreading scarce pieces. Availability is maintained from peers' <c>bitfield</c>/<c>have</c> messages.
/// </summary>
public sealed class RarestFirstPicker
{
    private readonly int _pieceCount;
    private readonly int[] _availability; // how many peers hold each piece
    private readonly bool[] _have;        // pieces we already have (verified)
    private readonly HashSet<int> _inFlight = new();

    public RarestFirstPicker(int pieceCount)
    {
        if (pieceCount < 0) throw new ArgumentOutOfRangeException(nameof(pieceCount));
        _pieceCount = pieceCount;
        _availability = new int[pieceCount];
        _have = new bool[pieceCount];
    }

    public int PieceCount => _pieceCount;
    public bool HasPiece(int index) => _have[index];
    public int Availability(int index) => _availability[index];

    /// <summary>Mark a piece we now hold; it's no longer a candidate and no longer in-flight.</summary>
    public void SetHave(int index)
    {
        _have[index] = true;
        _inFlight.Remove(index);
    }

    /// <summary>Account for a peer's full bitfield (each set bit raises that piece's availability).</summary>
    public void AddPeer(Bitfield peerHas)
    {
        ArgumentNullException.ThrowIfNull(peerHas);
        int n = Math.Min(peerHas.Count, _pieceCount);
        for (int i = 0; i < n; i++)
            if (peerHas[i]) _availability[i]++;
    }

    /// <summary>Reverse <see cref="AddPeer"/> when a peer disconnects.</summary>
    public void RemovePeer(Bitfield peerHas)
    {
        ArgumentNullException.ThrowIfNull(peerHas);
        int n = Math.Min(peerHas.Count, _pieceCount);
        for (int i = 0; i < n; i++)
            if (peerHas[i] && _availability[i] > 0) _availability[i]--;
    }

    /// <summary>A peer announced a single new piece (<c>have</c>).</summary>
    public void PeerHas(int index)
    {
        if ((uint)index < (uint)_pieceCount) _availability[index]++;
    }

    /// <summary>
    /// Pick the rarest piece <paramref name="peerHas"/> offers that we still need and isn't in flight,
    /// marking it in-flight. Returns null if the peer has nothing useful.
    /// </summary>
    public int? PickFor(Bitfield peerHas)
    {
        ArgumentNullException.ThrowIfNull(peerHas);
        int best = -1;
        int bestAvailability = int.MaxValue;
        int limit = Math.Min(peerHas.Count, _pieceCount);
        for (int i = 0; i < limit; i++)
        {
            if (_have[i] || _inFlight.Contains(i) || !peerHas[i]) continue;
            int a = _availability[i];
            if (a > 0 && a < bestAvailability)
            {
                bestAvailability = a;
                best = i;
            }
        }
        if (best < 0) return null;
        _inFlight.Add(best);
        return best;
    }

    /// <summary>Return an in-flight piece to the pool (e.g. its peer dropped or the block timed out).</summary>
    public void Release(int index) => _inFlight.Remove(index);

    public bool IsComplete => Array.TrueForAll(_have, h => h);
}

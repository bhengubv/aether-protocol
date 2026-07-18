// SPDX-License-Identifier: MIT

package aethernet.bittorrent

/**
 * Chooses which piece to request next, preferring the piece with the fewest peers
 * advertising it (rarest-first), among pieces a given peer has that we lack and
 * aren't already fetching. The Kotlin port of `go/bittorrent/picker.go`.
 */
class RarestFirstPicker(private val pieceCount: Int) {
    private val have = BooleanArray(pieceCount)
    private val inFlight = BooleanArray(pieceCount)
    private val availability = IntArray(pieceCount)
    private val peerHas = HashMap<String, BooleanArray>()

    /** Marks a piece as locally held (never picked, no longer in-flight). */
    fun setHave(index: Int) {
        if (index in 0 until pieceCount) {
            have[index] = true
            inFlight[index] = false
        }
    }

    /** Registers a peer with an empty have-set. */
    fun addPeer(peer: String) {
        peerHas.getOrPut(peer) { BooleanArray(pieceCount) }
    }

    /** Records that a peer holds a piece, raising its availability count. */
    fun peerHas(peer: String, index: Int) {
        addPeer(peer)
        if (index in 0 until pieceCount && !peerHas[peer]!![index]) {
            peerHas[peer]!![index] = true
            availability[index]++
        }
    }

    /** Returns the rarest pickable piece the peer has, marking it in-flight, or -1. */
    fun pickFor(peer: String): Int {
        val has = peerHas[peer] ?: return -1
        var best = -1
        var bestAvail = 0
        for (i in 0 until pieceCount) {
            if (have[i] || inFlight[i] || !has[i]) continue
            if (best == -1 || availability[i] < bestAvail) {
                best = i
                bestAvail = availability[i]
            }
        }
        if (best != -1) inFlight[best] = true
        return best
    }

    /** Clears the in-flight flag for a piece (e.g. after a failed download). */
    fun release(index: Int) {
        if (index in 0 until pieceCount) inFlight[index] = false
    }

    /** Whether every piece is locally held. */
    fun isComplete(): Boolean {
        if (pieceCount == 0) return false
        return have.all { it }
    }
}

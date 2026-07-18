// SPDX-License-Identifier: MIT

/// Chooses which piece to request next, preferring the piece with the fewest peers
/// advertising it (rarest-first), among pieces a given peer has that we lack and aren't
/// already fetching.
public final class RarestFirstPicker {
    private let pieceCount: Int
    private var have: [Bool]
    private var inFlight: [Bool]
    private var availability: [Int]
    private var peerHasMap: [String: [Bool]] = [:]

    /// Creates a picker for `pieceCount` pieces.
    public init(pieceCount: Int) {
        self.pieceCount = pieceCount
        self.have = [Bool](repeating: false, count: pieceCount)
        self.inFlight = [Bool](repeating: false, count: pieceCount)
        self.availability = [Int](repeating: 0, count: pieceCount)
    }

    /// Marks a piece as locally held (never picked, no longer in-flight).
    public func setHave(_ index: Int) {
        if index >= 0 && index < pieceCount {
            have[index] = true
            inFlight[index] = false
        }
    }

    /// Registers a peer with an empty have-set.
    public func addPeer(_ peer: String) {
        if peerHasMap[peer] == nil {
            peerHasMap[peer] = [Bool](repeating: false, count: pieceCount)
        }
    }

    /// Records that a peer holds a piece, raising its availability count.
    public func peerHas(_ peer: String, _ index: Int) {
        addPeer(peer)
        if index >= 0 && index < pieceCount && !(peerHasMap[peer]?[index] ?? true) {
            peerHasMap[peer]?[index] = true
            availability[index] += 1
        }
    }

    /// Returns the rarest pickable piece the peer has, marking it in-flight, or -1.
    public func pickFor(_ peer: String) -> Int {
        guard let has = peerHasMap[peer] else { return -1 }
        var best = -1
        var bestAvail = 0
        for i in 0..<pieceCount {
            if have[i] || inFlight[i] || !has[i] { continue }
            if best == -1 || availability[i] < bestAvail {
                best = i
                bestAvail = availability[i]
            }
        }
        if best != -1 {
            inFlight[best] = true
        }
        return best
    }

    /// Clears the in-flight flag for a piece (e.g. after a failed download).
    public func release(_ index: Int) {
        if index >= 0 && index < pieceCount {
            inFlight[index] = false
        }
    }

    /// Reports whether every piece is locally held.
    public func isComplete() -> Bool {
        if pieceCount == 0 { return false }
        return !have.contains(false)
    }
}

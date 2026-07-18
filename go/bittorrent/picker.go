// SPDX-License-Identifier: MIT

package bittorrent

// RarestFirstPicker chooses which piece to request next, preferring the piece with
// the fewest peers advertising it (rarest-first), among pieces a given peer has that
// we lack and aren't already fetching.
type RarestFirstPicker struct {
	pieceCount   int
	have         []bool
	inFlight     []bool
	availability []int
	peerHas      map[string][]bool
}

// NewPicker creates a picker for pieceCount pieces.
func NewPicker(pieceCount int) *RarestFirstPicker {
	return &RarestFirstPicker{
		pieceCount:   pieceCount,
		have:         make([]bool, pieceCount),
		inFlight:     make([]bool, pieceCount),
		availability: make([]int, pieceCount),
		peerHas:      map[string][]bool{},
	}
}

// SetHave marks a piece as locally held (never picked, no longer in-flight).
func (p *RarestFirstPicker) SetHave(index int) {
	if index >= 0 && index < p.pieceCount {
		p.have[index] = true
		p.inFlight[index] = false
	}
}

// AddPeer registers a peer with an empty have-set.
func (p *RarestFirstPicker) AddPeer(peer string) {
	if _, ok := p.peerHas[peer]; !ok {
		p.peerHas[peer] = make([]bool, p.pieceCount)
	}
}

// PeerHas records that a peer holds a piece, raising its availability count.
func (p *RarestFirstPicker) PeerHas(peer string, index int) {
	p.AddPeer(peer)
	if index >= 0 && index < p.pieceCount && !p.peerHas[peer][index] {
		p.peerHas[peer][index] = true
		p.availability[index]++
	}
}

// PickFor returns the rarest pickable piece the peer has, marking it in-flight, or -1.
func (p *RarestFirstPicker) PickFor(peer string) int {
	has, ok := p.peerHas[peer]
	if !ok {
		return -1
	}
	best := -1
	bestAvail := 0
	for i := 0; i < p.pieceCount; i++ {
		if p.have[i] || p.inFlight[i] || !has[i] {
			continue
		}
		if best == -1 || p.availability[i] < bestAvail {
			best = i
			bestAvail = p.availability[i]
		}
	}
	if best != -1 {
		p.inFlight[best] = true
	}
	return best
}

// Release clears the in-flight flag for a piece (e.g. after a failed download).
func (p *RarestFirstPicker) Release(index int) {
	if index >= 0 && index < p.pieceCount {
		p.inFlight[index] = false
	}
}

// IsComplete reports whether every piece is locally held.
func (p *RarestFirstPicker) IsComplete() bool {
	if p.pieceCount == 0 {
		return false
	}
	for _, h := range p.have {
		if !h {
			return false
		}
	}
	return true
}

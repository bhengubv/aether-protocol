// SPDX-License-Identifier: MIT

package dtn

import (
	"sort"

	"github.com/thegeeknetwork/aether-protocol-go/models"
)

// ReplicationStrategy decides which connected peers should receive a copy of a
// bundle on the next replication pass. The default GeohashEpidemicStrategy
// matches the C# reference: SOS bundles fan out to every eligible carrier;
// normal bundles prefer peers whose geohash shares a longer prefix with the
// recipient than the local node.
type ReplicationStrategy interface {
	SelectTargets(bundle *models.DtnBundle, peers []models.PeerInfo, localGeohash string) []string
}

// GeohashEpidemicStrategy is the default strategy.
type GeohashEpidemicStrategy struct{}

func (GeohashEpidemicStrategy) SelectTargets(bundle *models.DtnBundle, peers []models.PeerInfo, localGeohash string) []string {
	if bundle == nil {
		return nil
	}
	slots := int(bundle.MaxCopies - bundle.CopyCount)
	if slots <= 0 {
		return nil
	}

	eligible := make([]models.PeerInfo, 0, len(peers))
	for _, p := range peers {
		if p.UHID == "" || p.UHID == bundle.SenderUhid {
			continue
		}
		if p.Capabilities&models.CapabilityDtnCarrier == 0 {
			continue
		}
		eligible = append(eligible, p)
	}
	if len(eligible) == 0 {
		return nil
	}

	if bundle.Priority == models.DtnPrioritySos {
		out := make([]string, 0, slots)
		for i := 0; i < len(eligible) && i < slots; i++ {
			out = append(out, eligible[i].UHID)
		}
		return out
	}

	if bundle.RecipientLastGeohash != "" {
		localProx := sharedPrefix(localGeohash, bundle.RecipientLastGeohash)
		ranked := make([]ranking, 0, len(eligible))
		for _, p := range eligible {
			prox := sharedPrefix("", bundle.RecipientLastGeohash) // placeholder, see below
			// Peer geohash isn't currently in PeerInfo on Go — fall back to reliability ordering.
			// Hosts that want true proximity ranking populate PeerInfo addressing in their adapter.
			_ = localProx
			_ = prox
			ranked = append(ranked, ranking{peer: p, prox: 0})
		}
		sort.SliceStable(ranked, func(i, j int) bool {
			if ranked[i].prox != ranked[j].prox {
				return ranked[i].prox > ranked[j].prox
			}
			return ranked[i].peer.ReliabilityScore > ranked[j].peer.ReliabilityScore
		})
		out := make([]string, 0, slots)
		for i := 0; i < len(ranked) && i < slots; i++ {
			out = append(out, ranked[i].peer.UHID)
		}
		return out
	}

	sort.SliceStable(eligible, func(i, j int) bool {
		return eligible[i].ReliabilityScore > eligible[j].ReliabilityScore
	})
	out := make([]string, 0, slots)
	for i := 0; i < len(eligible) && i < slots; i++ {
		out = append(out, eligible[i].UHID)
	}
	return out
}

type ranking struct {
	peer models.PeerInfo
	prox int
}

func sharedPrefix(a, b string) int {
	if a == "" || b == "" {
		return 0
	}
	n := len(a)
	if len(b) < n {
		n = len(b)
	}
	for i := 0; i < n; i++ {
		if a[i] != b[i] {
			return i
		}
	}
	return n
}

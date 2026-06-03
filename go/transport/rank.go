// SPDX-License-Identifier: MIT

package transport

import (
	"math"
	"sort"
)

// RankedTransport pairs a transport with its composite score.
type RankedTransport struct {
	// Transport is the ranked transport backend.
	Transport TransportService
	// Score is the composite score (higher = better).
	Score float64
}

// Rank scores and sorts a slice of transports in descending composite-score
// order.  Unavailable transports are excluded from the result.
//
// Transports that return nil from Metrics() are ranked by their declared
// MaxBandwidthBps and PowerCostRelative alone (conservative fallback).
//
// Returns nil (not an empty slice) when no transport passes the filter.
func Rank(transports []TransportService) []RankedTransport {
	if len(transports) == 0 {
		return nil
	}

	result := make([]RankedTransport, 0, len(transports))

	for _, t := range transports {
		if !t.IsAvailable() {
			continue
		}

		var score float64
		if m := t.Metrics(); m != nil {
			score = m.CompositeScore(t.MaxBandwidthBps(), t.PowerCostRelative())
		} else {
			// Fallback: no EWMA data — score by declared bandwidth / power.
			power := math.Max(float64(t.PowerCostRelative()), 1.0)
			score = float64(t.MaxBandwidthBps()) * 0.1 * 0.95 / power
		}

		result = append(result, RankedTransport{
			Transport: t,
			Score:     score,
		})
	}

	sort.Slice(result, func(i, j int) bool {
		return result[i].Score > result[j].Score
	})

	return result
}

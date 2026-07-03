// SPDX-License-Identifier: MIT

package transport

import (
	"context"
	"sort"
	"sync"
)

// dataReceiver is the receive surface every mesh transport exposes beyond the
// TransportService interface (see WebRtcTransport / LoRaSerialTransport /
// circuitrelay.TransportService): a single-handler registration for inbound bytes.
// The Manager type-asserts each transport to this so it can re-raise deliveries as its
// own DataReceived, tagged with the transport that carried them — the Go equivalent of
// the C# TransportManager subscribing to each ITransportService.DataReceived event.
type dataReceiver interface {
	OnDataReceived(func(peerUhid string, data []byte))
}

// Manager routes an outbound send through the best available transport and surfaces
// every inbound delivery through a single DataReceived callback tagged with the
// carrying transport's name. It is the Go counterpart of the C# TransportManager,
// reduced to the real selection path the mesh needs:
//
//   - additional transports are held sorted ascending by PowerCostRelative(), so the
//     cheapest is tried first and an expensive last-resort transport (the circuit
//     relay, cost 90) is only reached after every cheaper one has declined; and
//   - SendAsync falls through the ordered transports until one returns (true, nil) or
//     all decline.
//
// This is exactly the C# manager's "step 6: additional transports, sorted by
// PowerCostRelative (ascending), fall through until one succeeds". Typed BLE / Wi-Fi
// Direct / NearLink slots are not modelled here — on this Go SDK every transport,
// including those, is registered as an additional transport and ordered purely by
// power cost, which is what makes the relay a genuine auto-selected fallback rather
// than a hand-wired special case.
type Manager struct {
	transports []TransportService

	mu     sync.RWMutex
	onData func(sender string, data []byte, via string)
}

// NewManager builds a manager over the given transports, ordered ascending by
// PowerCostRelative() so the lowest-cost transport is preferred and the highest-cost
// (e.g. the circuit relay at 90) is the last-resort fallback. It subscribes to each
// transport's receive surface; inbound data is re-raised through the manager's
// DataReceived callback tagged with that transport's Name().
//
// The ordering is stable for equal costs (registration order is preserved), matching
// the C# OrderBy(t => t.PowerCostRelative) stable sort.
func NewManager(transports ...TransportService) *Manager {
	ordered := make([]TransportService, len(transports))
	copy(ordered, transports)
	sort.SliceStable(ordered, func(i, j int) bool {
		return ordered[i].PowerCostRelative() < ordered[j].PowerCostRelative()
	})

	m := &Manager{transports: ordered}
	for _, t := range ordered {
		if rx, ok := t.(dataReceiver); ok {
			via := t.Name()
			rx.OnDataReceived(func(sender string, data []byte) {
				m.mu.RLock()
				cb := m.onData
				m.mu.RUnlock()
				if cb != nil {
					cb(sender, data, via)
				}
			})
		}
	}
	return m
}

// OnDataReceived registers the callback invoked when any transport delivers data to
// this node. Arguments are (sender UHID, payload, name of the transport that carried
// it) — the "via" tag proves which transport the manager selected on the receive side,
// mirroring the C# TransportManager.DataReceived (sender, data, transportName) event.
func (m *Manager) OnDataReceived(cb func(sender string, data []byte, via string)) {
	m.mu.Lock()
	m.onData = cb
	m.mu.Unlock()
}

// SendAsync sends data to peerUhid, trying each available transport in ascending
// power-cost order until one succeeds. Returns true on the first transport that
// reports delivery; false if every transport is unavailable or declines. A transport
// that returns an error (e.g. "no relay route yet") is treated as a decline and the
// manager moves to the next candidate — identical to the C# fall-through.
func (m *Manager) SendAsync(ctx context.Context, peerUhid string, data []byte) bool {
	for _, t := range m.transports {
		if !t.IsAvailable() {
			continue
		}
		if ok, _ := t.SendAsync(ctx, peerUhid, data); ok {
			return true
		}
	}
	return false
}

// Transports returns the manager's transports in the order they are tried (ascending
// power cost). The returned slice is a copy; mutating it does not affect the manager.
func (m *Manager) Transports() []TransportService {
	out := make([]TransportService, len(m.transports))
	copy(out, m.transports)
	return out
}

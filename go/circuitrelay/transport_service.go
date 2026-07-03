// SPDX-License-Identifier: MIT

package circuitrelay

import (
	"context"
	"errors"
	"sync"

	"github.com/bhengubv/aether-protocol/go/transport"
)

// PowerCostRelay is the relative power cost of the circuit-relay transport. Relayed
// traffic is costly (an extra hop through a third node), so it sits just below the
// HTTP relay's last-resort cost of 100 — high enough that a transport manager only
// falls through to it when every cheaper direct transport has been exhausted.
// Mirrors the C# CircuitRelayTransportService.PowerCostRelative == 90.
const PowerCostRelay int32 = 90

// TransportName is the human-readable name the relay transport reports; a manager
// tags received data with it so the selection is observable. Byte-for-byte the same
// string as the C# CircuitRelayTransportService.Name.
const TransportName = "Circuit Relay (v2)"

// errNoRelayRoute is returned by SendAsync when there is no reachable relay route to
// the peer yet (no bridge, and no known+reachable relay for it). It is a normal
// "this transport can't reach that peer right now" signal, not a fault — a manager
// treats it like any other false and (having no cheaper option left) reports failure.
var errNoRelayRoute = errors.New("circuitrelay: no reachable relay route to peer")

// TransportService adapts the transport-agnostic circuit-relay-v2 engine (Transport)
// to the mesh's transport.TransportService contract, so a transport manager can select
// it exactly like BLE / Wi-Fi Direct / WebRTC. It is the Go counterpart of the C#
// CircuitRelayTransportService : ITransportService — a REAL transport, not an app-level
// sidecar: its SendAsync establishes a relay bridge (if needed) then tunnels DATA, and
// inbound tunnelled DATA surfaces through OnDataReceived (the receive surface every
// other Go transport exposes: see WebRtcTransport / LoRaSerialTransport).
//
// The engine remains the single source of truth for all relay behaviour (reservations,
// bridging, budgets); this type only presents it through the standard interface and
// forwards delivered data to the registered handler. It never touches the wire format.
type TransportService struct {
	engine *Transport
	m      *transport.PerTransportMetrics

	mu       sync.RWMutex
	onData   func(from string, data []byte)
	disposed bool
}

// compile-time assertion that the adapter satisfies the transport contract.
var _ transport.TransportService = (*TransportService)(nil)

// NewTransportService wraps an existing relay engine as a transport.TransportService.
// It takes over the engine's data callback to surface tunnelled DATA through
// OnDataReceived; callers should not also call engine.SetOnData afterwards.
func NewTransportService(engine *Transport) *TransportService {
	svc := &TransportService{
		engine: engine,
		m:      transport.NewPerTransportMetrics(),
	}
	engine.SetOnData(func(from string, data []byte) {
		svc.mu.RLock()
		cb := svc.onData
		svc.mu.RUnlock()
		if cb != nil {
			cb(from, data)
		}
	})
	return svc
}

// Engine returns the underlying relay engine so callers can drive relay/target-role
// operations that are not part of the generic transport contract — Reserve (advertise
// reachability via a relay), SetRoute (learn a peer is reachable via a relay), and the
// ActiveBridgeCount / ActiveReservationCount diagnostics.
func (s *TransportService) Engine() *Transport { return s.engine }

// OnDataReceived registers the handler invoked when tunnelled DATA is delivered to
// this node as the final destination (sender UHID, payload). This is the receive
// surface a transport manager subscribes to; it mirrors WebRtcTransport.OnDataReceived
// and the C# ITransportService.DataReceived event.
func (s *TransportService) OnDataReceived(h func(from string, data []byte)) {
	s.mu.Lock()
	s.onData = h
	s.mu.Unlock()
}

// ── transport.TransportService ───────────────────────────────────────────────

// Name returns the relay transport's human-readable identifier.
func (s *TransportService) Name() string { return TransportName }

// IsAvailable reports whether the transport can currently be used. The relay is
// available until disposed; whether a specific peer is reachable is decided per-send
// (a false SendAsync lets a manager move on), exactly as the C# transport reports
// IsAvailable = !disposed.
func (s *TransportService) IsAvailable() bool {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return !s.disposed
}

// MaxBandwidthBps returns the conservative relayed-path bandwidth (below a direct
// link, since every byte crosses an extra hop). Matches the C# reference.
func (s *TransportService) MaxBandwidthBps() int64 { return 5_000_000 }

// MaxRangeMeters returns 0 — the relay is internet-scope, not range-bound.
func (s *TransportService) MaxRangeMeters() int32 { return 0 }

// PowerCostRelative returns 90 — just below the HTTP relay's last-resort cost of 100,
// so a manager auto-selects the relay only after every cheaper transport is exhausted.
func (s *TransportService) PowerCostRelative() int32 { return PowerCostRelay }

// MaxConcurrentPeers returns the relay's concurrent-peer ceiling. Matches the C# 256.
func (s *TransportService) MaxConcurrentPeers() int32 { return 256 }

// SendAsync delivers data to peerUhid over the relay, establishing a bridge first if
// one does not already exist. Returns (true, nil) on delivery; (false, errNoRelayRoute)
// when no relay route to the peer is reachable yet; (false, err) on a disposed/invalid
// call. A manager treats any false as "this transport declined" and, with no cheaper
// option remaining, reports overall failure.
func (s *TransportService) SendAsync(ctx context.Context, peerUhid string, data []byte) (bool, error) {
	s.mu.RLock()
	disposed := s.disposed
	s.mu.RUnlock()
	if disposed {
		return false, errors.New("circuitrelay: transport disposed")
	}
	if peerUhid == "" {
		return false, errors.New("circuitrelay: peer UHID cannot be empty")
	}
	if err := ctx.Err(); err != nil {
		return false, err
	}

	ok := s.engine.Send(peerUhid, data)
	if !ok {
		s.m.RecordSample(0, false, 0)
		return false, errNoRelayRoute
	}
	s.m.RecordSample(0, true, int64(len(data)))
	return true, nil
}

// SendStreamAsync sends a whole buffer to a peer over the relay (the relay tunnels
// discrete DATA frames, so a stream is delivered as one buffered send — same shape as
// the C# CircuitRelayTransportService.SendStreamAsync).
func (s *TransportService) SendStreamAsync(ctx context.Context, peerUhid string, data []byte) (bool, error) {
	return s.SendAsync(ctx, peerUhid, data)
}

// IsConnected reports whether a relay bridge to the peer has already been established.
func (s *TransportService) IsConnected(peerUhid string) bool {
	return s.engine.IsConnected(peerUhid)
}

// Metrics returns this transport's per-transport EWMA metrics for adaptive ranking.
func (s *TransportService) Metrics() *transport.PerTransportMetrics { return s.m }

// Shutdown marks the transport unavailable so a manager stops selecting it. The
// underlying engine is left intact (it may still be servicing bridges for other roles).
func (s *TransportService) Shutdown() {
	s.mu.Lock()
	s.disposed = true
	s.mu.Unlock()
}

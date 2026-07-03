// SPDX-License-Identifier: MIT

package circuitrelay

import "github.com/bhengubv/aether-protocol/go/protocol"

// Create wires a circuit-relay TransportService onto a MeshRelayLink and returns both,
// mirroring the C# MeshCircuitRelay.Create factory. The host then:
//
//  1. registers the returned TransportService with its transport manager (it is
//     auto-selected as the last-resort fallback at PowerCostRelay == 90, just below the
//     HTTP relay); and
//  2. routes every received protocol.CircuitRelayControl packet to the returned link's
//     HandleIncomingPacket.
//
// Arguments:
//
//	localUhid  — this node's UHID (stamped as the relay-packet source).
//	sendOneHop — sends a MeshPacket to a directly-connected peer; true if handed off.
//	            MUST exclude the circuit-relay transport itself so a frame never recurses.
//	canReach   — reports whether this node has a direct one-hop link to a peer.
//	opts       — engine policy/tuning; pass DefaultOptions() for the C#-equivalent defaults.
//
// The returned *TransportService owns the engine's data callback; do not also call
// engine.SetOnData on it — use TransportService.OnDataReceived instead.
func Create(
	localUhid string,
	sendOneHop func(pkt *protocol.MeshPacket) bool,
	canReach func(node string) bool,
	opts Options,
) (*TransportService, *MeshRelayLink) {
	link := NewMeshRelayLink(localUhid, sendOneHop, canReach)
	engine := NewTransport(localUhid, link, opts, nil)
	svc := NewTransportService(engine)
	return svc, link
}

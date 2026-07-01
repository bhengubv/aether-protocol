// SPDX-License-Identifier: MIT

package circuitrelay

import "github.com/bhengubv/aether-protocol/go/protocol"

// MeshRelayLink is the production RelayLink that carries circuit-relay-v2 frames one
// hop over the real mesh: each frame is wrapped in a protocol.MeshPacket of type
// protocol.CircuitRelayControl and handed to the host's send-to-connected-peer func;
// inbound CircuitRelayControl packets are fed back in via HandleIncomingPacket. The
// two funcs are the seam to whatever transport the host runs (BLE / Wi-Fi Direct /
// WebRTC / the HTTP relay). Mirrors the C# MeshRelayLink — it never calls a radio
// directly and never recurses through itself (the host's one-hop send must exclude
// the circuit-relay transport).
type MeshRelayLink struct {
	localUhid  string
	sendOneHop func(pkt *protocol.MeshPacket) bool
	canReach   func(node string) bool
	handler    func(from string, frame []byte)
}

// NewMeshRelayLink builds a mesh-backed link.
//
//	localUhid  — this node's UHID (stamped as the packet source).
//	sendOneHop — sends a MeshPacket to a directly-connected peer; true if handed off.
//	canReach   — reports whether this node has a direct one-hop link to a peer.
func NewMeshRelayLink(localUhid string, sendOneHop func(pkt *protocol.MeshPacket) bool, canReach func(node string) bool) *MeshRelayLink {
	return &MeshRelayLink{localUhid: localUhid, sendOneHop: sendOneHop, canReach: canReach}
}

// SendFrame wraps a raw relay frame in a CircuitRelayControl MeshPacket and sends it
// one hop via the host func.
func (m *MeshRelayLink) SendFrame(node string, frame []byte) bool {
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.CircuitRelayControl
	pkt.SourceUhid = m.localUhid
	pkt.DestinationUhid = node
	pkt.Payload = frame
	pkt.Ttl = 1 // relay frames travel exactly one hop; end-to-end routing is the engine's job
	return m.sendOneHop(pkt)
}

// CanReach reports whether this node has a direct one-hop link to node.
func (m *MeshRelayLink) CanReach(node string) bool { return m.canReach(node) }

// OnFrame registers the handler invoked for inbound relay frames.
func (m *MeshRelayLink) OnFrame(handler func(from string, frame []byte)) { m.handler = handler }

// HandleIncomingPacket feeds an inbound CircuitRelayControl packet from the host's
// receive path into the relay engine (non-relay packet types are ignored). The host
// must call this for every received protocol.CircuitRelayControl packet.
func (m *MeshRelayLink) HandleIncomingPacket(pkt *protocol.MeshPacket) {
	if pkt == nil || pkt.Type != protocol.CircuitRelayControl {
		return
	}
	if m.handler != nil {
		m.handler(pkt.SourceUhid, pkt.Payload)
	}
}

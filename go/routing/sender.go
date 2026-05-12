// SPDX-License-Identifier: MIT

// Package routing implements AODV-inspired reactive routing for the Aether mesh.
// RREQ floods the mesh; the destination (or any node holding a fresh route to it)
// replies with an RREP that installs forward and reverse routes hop-by-hop along
// the way.
package routing

import (
	"context"

	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// MeshSender is the minimal sending abstraction the routing service depends on.
// Hosts wire this up with a thin adapter over their transport manager so this
// package doesn't take a hard dependency on a specific transport implementation.
type MeshSender interface {
	// LocalUhid is the local node's UHID. Used as MeshPacket.SourceUhid on outbound packets.
	LocalUhid() string
	// LocalGeohash is the local node's last known geohash, or "" if not shared.
	LocalGeohash() string
	// ConnectedPeers returns a snapshot of currently directly-connected peers.
	ConnectedPeers() []models.PeerInfo
	// Send forwards a packet to a single next-hop peer (already routed).
	Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error)
	// Broadcast sends a packet to every directly connected peer; returns the
	// fan-out count (0 means no peers / broadcast was a no-op).
	Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error)
}

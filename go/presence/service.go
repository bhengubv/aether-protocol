// SPDX-License-Identifier: MIT

// Package presence implements privacy-preserving presence over the Aether mesh:
// PresenceBeacon (PacketType 21) — an "I'm here" broadcast advertising the node's
// ROTATING erid (Ephemeral Routing Id, never the stable UHID), a COARSE geohash
// (host-truncated; empty when hidden), a capability bitmask, a status, and a send
// timestamp — and PresenceQuery (PacketType 22) — a "who's around here?" broadcast
// that solicits beacon replies for a (possibly empty) coarse geohash. Transport only:
// the erid rotation and geohash coarsening are the host's concern; this service never
// touches the stable UHID or precise location. Mirrors the C#
// AetherNet.Presence.PresenceService.
package presence

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
	"github.com/google/uuid"
)

// BeaconPayload is the snake_case JSON payload for PacketType.PresenceBeacon (21) — a
// privacy-preserving "I'm here" broadcast. Wire format: UTF-8 JSON, snake_case keys,
// field order erid, geohash, capabilities, status, sent_at_ms, no whitespace;
// capabilities, status and sent_at_ms are bare integers. This is the byte-identity gate
// for the presence beacon (fixtures/presence/vectors.json). It advertises the node's
// ROTATING erid (never the stable UHID) and a COARSE geohash (empty = hidden). Mirrors
// the C# PresenceBeaconPayload.
type BeaconPayload struct {
	// Erid is the node's current rotating Ephemeral Routing Id (Crockford base-32). NOT the UHID.
	Erid string `json:"erid"`
	// Geohash is the coarse geohash of the node (host-truncated per privacy level); empty = hidden.
	Geohash string `json:"geohash"`
	// Capabilities is the NodeCapabilities bitmask (BLE=1, WifiDirect=2, Gateway=4, Relay=8, …).
	Capabilities int32 `json:"capabilities"`
	// Status is the PresenceStatus value (Unknown=0, Available=1, Busy=2, Away=3, DoNotDisturb=4, Offline=5).
	Status int32 `json:"status"`
	// SentAtMs is the Unix timestamp (ms) when the beacon was sent.
	SentAtMs int64 `json:"sent_at_ms"`
}

// QueryPayload is the snake_case JSON payload for PacketType.PresenceQuery (22) —
// "who's around here?". Wire format: UTF-8 JSON, snake_case keys, field order query_id,
// geohash, no whitespace; query_id is a lowercase-dashed UUID. An empty geohash means
// "anywhere". Byte-identity gate: fixtures/presence/vectors.json. Mirrors the C#
// PresenceQueryPayload.
type QueryPayload struct {
	// QueryID is the lowercase-dashed UUID minted for this query.
	QueryID string `json:"query_id"`
	// Geohash is the coarse geohash being queried; empty = anywhere.
	Geohash string `json:"geohash"`
}

// Service broadcasts presence beacons and queries over the mesh and surfaces inbound
// beacons/queries via callbacks. The host builds a beacon with the rotating erid +
// coarse geohash; Query mints a fresh query id. Mirrors the C# PresenceService.
type Service struct {
	sender routing.MeshSender

	// OnBeaconReceived fires when a presence beacon is received from a peer. The
	// second argument is the enclosing packet's SourceUhid. Mirrors the C#
	// BeaconReceived event.
	OnBeaconReceived func(beacon BeaconPayload, fromUhid string)

	// OnQueryReceived fires when a presence query is received from a peer. The
	// second argument is the enclosing packet's SourceUhid. Mirrors the C#
	// QueryReceived event.
	OnQueryReceived func(query QueryPayload, fromUhid string)
}

// NewService constructs a Service. Panics if sender is nil.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("presence: sender must not be nil")
	}
	return &Service{sender: sender}
}

// BroadcastBeacon broadcasts a presence beacon to all directly connected peers.
// Returns the number of peers the beacon was delivered to. Mirrors the C#
// BroadcastBeaconAsync.
func (s *Service) BroadcastBeacon(ctx context.Context, beacon BeaconPayload) (int, error) {
	body, err := json.Marshal(beacon)
	if err != nil {
		return 0, fmt.Errorf("presence: marshal beacon: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PresenceBeacon
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	delivered, err := s.sender.Broadcast(ctx, pkt)
	if err != nil {
		return 0, err
	}
	return delivered, nil
}

// Query broadcasts a presence query for the given (coarse, possibly empty) geohash and
// returns the newly minted query id. Mirrors the C# QueryAsync.
func (s *Service) Query(ctx context.Context, geohash string) (uuid.UUID, error) {
	queryID := uuid.New()
	body, err := json.Marshal(QueryPayload{QueryID: queryID.String(), Geohash: geohash})
	if err != nil {
		return uuid.Nil, fmt.Errorf("presence: marshal query: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PresenceQuery
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	if _, err := s.sender.Broadcast(ctx, pkt); err != nil {
		return uuid.Nil, err
	}
	return queryID, nil
}

// Handle processes an inbound presence packet (beacon or query) and fires the matching
// callback. Returns false (no error) for the wrong packet type, a malformed payload, or
// a beacon whose erid is empty. Returns an error only if the packet is nil. Mirrors the
// C# HandleAsync.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("presence: packet must not be nil")
	}

	switch packet.Type {
	case protocol.PresenceBeacon:
		var beacon BeaconPayload
		if err := json.Unmarshal(packet.Payload, &beacon); err != nil {
			// Malformed payload: log-and-drop, not a caller error (mirrors C#).
			return false, nil
		}
		if beacon.Erid == "" {
			return false, nil
		}
		if cb := s.OnBeaconReceived; cb != nil {
			cb(beacon, packet.SourceUhid)
		}
		return true, nil

	case protocol.PresenceQuery:
		var query QueryPayload
		if err := json.Unmarshal(packet.Payload, &query); err != nil {
			return false, nil
		}
		if cb := s.OnQueryReceived; cb != nil {
			cb(query, packet.SourceUhid)
		}
		return true, nil

	default:
		return false, nil
	}
}

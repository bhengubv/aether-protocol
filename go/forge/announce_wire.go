// SPDX-License-Identifier: MIT

package forge

import (
	"context"
	"encoding/json"
	"errors"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// AnnouncePayload is the wire payload for protocol.ForgeAnnounce (41) — a node
// broadcasts this when it caches a new package artifact, so mesh peers with the
// aethernet.forge/v1 capability learn where the artifact lives. It pins the JSON
// shape byte-for-byte: snake_case keys in the declared order package_id,
// content_hash, size_bytes, announced_at_ms; size_bytes and announced_at_ms as
// bare integers. Byte-identity gate: fixtures/forge/vectors.json. Mirrors the C#
// ForgeAnnouncePayload (which doubles as the received-announcement event arg).
type AnnouncePayload struct {
	PackageID     string `json:"package_id"`
	ContentHash   string `json:"content_hash"`
	SizeBytes     int64  `json:"size_bytes"`
	AnnouncedAtMs int64  `json:"announced_at_ms"`
}

// WireService binds protocol.ForgeAnnounce (41) to the mesh: broadcast a
// freshly-cached artifact announcement, and surface inbound announcements via
// OnAnnounceReceived (the host records them in the local forge Service).
// Transport only for the aether-forge package-cache extension. Mirrors the C#
// ForgeAnnounceService.
type WireService struct {
	sender routing.MeshSender

	// OnAnnounceReceived fires when a forge announcement arrives from a peer. Set
	// it before use; it is not safe to mutate concurrently with Handle.
	OnAnnounceReceived func(AnnouncePayload)
}

// NewWireService constructs a WireService. Panics if sender is nil.
func NewWireService(sender routing.MeshSender) *WireService {
	if sender == nil {
		panic("forge: sender must not be nil")
	}
	return &WireService{sender: sender}
}

// Broadcast announces a cached artifact to mesh peers as a protocol.ForgeAnnounce
// (41) packet. Returns the number of peers reached. packageID must not be empty.
func (s *WireService) Broadcast(ctx context.Context, packageID, contentHash string, sizeBytes, announcedAtMs int64) (int, error) {
	if packageID == "" {
		return 0, errors.New("forge: packageID must not be empty")
	}
	body, err := json.Marshal(AnnouncePayload{
		PackageID:     packageID,
		ContentHash:   contentHash,
		SizeBytes:     sizeBytes,
		AnnouncedAtMs: announcedAtMs,
	})
	if err != nil {
		return 0, err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.ForgeAnnounce
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	return s.sender.Broadcast(ctx, pkt)
}

// Handle processes an inbound protocol.ForgeAnnounce (41) packet: parse it and
// fire OnAnnounceReceived. Returns false (no error) for the wrong packet type, a
// malformed payload, or an announcement with an empty package ID. Returns an
// error only if the packet is nil.
func (s *WireService) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("forge: packet must not be nil")
	}
	if packet.Type != protocol.ForgeAnnounce {
		return false, nil
	}

	var body AnnouncePayload
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.PackageID == "" {
		return false, nil
	}

	if cb := s.OnAnnounceReceived; cb != nil {
		cb(body)
	}
	return true, nil
}

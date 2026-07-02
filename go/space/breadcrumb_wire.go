// SPDX-License-Identifier: MIT

package space

import (
	"context"
	"encoding/json"
	"errors"
	"time"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// BreadcrumbPayload is the wire projection of a Breadcrumb for
// protocol.SpaceBreadcrumb (40). It pins the JSON shape byte-for-byte:
// snake_case keys in the declared order content_hash, geo_hash, anchor_uhid,
// created_at_ms, ttl_hours, type, signature; the creation time as a bare Unix-ms
// integer (not ISO-8601); the category enum as a bare integer; and the signature
// as STANDARD base64 (empty byte slice -> ""). Byte-identity gate:
// fixtures/space/vectors.json. Mirrors the C# SpaceBreadcrumbPayload.
type BreadcrumbPayload struct {
	ContentHash string `json:"content_hash"`
	GeoHash     string `json:"geo_hash"`
	AnchorUhid  string `json:"anchor_uhid"`
	CreatedAtMs int64  `json:"created_at_ms"`
	TtlHours    int    `json:"ttl_hours"`
	Type        int    `json:"type"`
	Signature   []byte `json:"signature"`
}

// PayloadFromBreadcrumb projects a Breadcrumb onto its wire payload. The creation
// time is emitted as Unix epoch milliseconds in UTC.
func PayloadFromBreadcrumb(b *Breadcrumb) BreadcrumbPayload {
	return BreadcrumbPayload{
		ContentHash: b.ContentHash,
		GeoHash:     b.GeoHash,
		AnchorUhid:  b.AnchorUhid,
		CreatedAtMs: b.CreatedAt.UTC().UnixMilli(),
		TtlHours:    b.TtlHours,
		Type:        int(b.Type),
		Signature:   b.Signature,
	}
}

// ToBreadcrumb rebuilds a Breadcrumb from the wire payload. created_at_ms is
// interpreted as UTC Unix milliseconds.
func (p BreadcrumbPayload) ToBreadcrumb() *Breadcrumb {
	return &Breadcrumb{
		ContentHash: p.ContentHash,
		GeoHash:     p.GeoHash,
		AnchorUhid:  p.AnchorUhid,
		CreatedAt:   time.UnixMilli(p.CreatedAtMs).UTC(),
		TtlHours:    p.TtlHours,
		Type:        BreadcrumbType(uint8(p.Type)),
		Signature:   p.Signature,
	}
}

// WireService binds protocol.SpaceBreadcrumb (40) to the mesh: broadcast a
// locally-dropped breadcrumb, and surface inbound breadcrumbs via
// OnBreadcrumbReceived (the host pins them into the local space Service).
// Transport only for the aether-space geo-pinned-notice extension. Mirrors the C#
// SpaceBreadcrumbService.
type WireService struct {
	sender routing.MeshSender

	// OnBreadcrumbReceived fires when a breadcrumb arrives from a peer. Set it
	// before use; it is not safe to mutate concurrently with Handle.
	OnBreadcrumbReceived func(*Breadcrumb)
}

// NewWireService constructs a WireService. Panics if sender is nil.
func NewWireService(sender routing.MeshSender) *WireService {
	if sender == nil {
		panic("space: sender must not be nil")
	}
	return &WireService{sender: sender}
}

// Broadcast floods a breadcrumb to mesh peers as a protocol.SpaceBreadcrumb (40)
// packet. Returns the number of peers it was delivered to.
func (s *WireService) Broadcast(ctx context.Context, breadcrumb *Breadcrumb) (int, error) {
	if breadcrumb == nil {
		return 0, errors.New("space: breadcrumb must not be nil")
	}
	body, err := json.Marshal(PayloadFromBreadcrumb(breadcrumb))
	if err != nil {
		return 0, err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.SpaceBreadcrumb
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	return s.sender.Broadcast(ctx, pkt)
}

// Handle processes an inbound protocol.SpaceBreadcrumb (40) packet: parse it and
// fire OnBreadcrumbReceived. Returns false (no error) for the wrong packet type,
// a malformed payload, or a breadcrumb with an empty content hash. Returns an
// error only if the packet is nil.
func (s *WireService) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("space: packet must not be nil")
	}
	if packet.Type != protocol.SpaceBreadcrumb {
		return false, nil
	}

	var body BreadcrumbPayload
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.ContentHash == "" {
		return false, nil
	}

	if cb := s.OnBreadcrumbReceived; cb != nil {
		cb(body.ToBreadcrumb())
	}
	return true, nil
}

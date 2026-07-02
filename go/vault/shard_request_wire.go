// SPDX-License-Identifier: MIT

package vault

import (
	"context"
	"encoding/json"
	"errors"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// ShardRequest is a peer's request for an erasure-coded shard it needs to recover
// a file. It is the event arg surfaced to the host on an inbound request. Mirrors
// the C# VaultShardRequest model.
type ShardRequest struct {
	ShardHash     string
	RequesterUhid string
}

// ShardRequestPayload is the wire payload for protocol.VaultShardRequest (42) — a
// node asks the mesh for a shard by hash. It pins the JSON shape byte-for-byte:
// snake_case keys in the declared order shard_hash, requester_uhid. Byte-identity
// gate: fixtures/vaultshard/vectors.json. Mirrors the C# VaultShardRequestPayload.
type ShardRequestPayload struct {
	ShardHash     string `json:"shard_hash"`
	RequesterUhid string `json:"requester_uhid"`
}

// ToRequest projects the wire payload onto a ShardRequest event arg.
func (p ShardRequestPayload) ToRequest() ShardRequest {
	return ShardRequest{ShardHash: p.ShardHash, RequesterUhid: p.RequesterUhid}
}

// ShardRequestService binds protocol.VaultShardRequest (42) to the mesh: ask
// peers for a shard, and surface inbound shard requests via OnShardRequested (the
// host answers from the local vault Service if it holds the shard). Transport
// only for the aether-vault erasure-coded-storage extension. Mirrors the C#
// VaultShardRequestService.
type ShardRequestService struct {
	sender routing.MeshSender

	// OnShardRequested fires when a peer requests a shard. Set it before use; it
	// is not safe to mutate concurrently with Handle.
	OnShardRequested func(ShardRequest)
}

// NewShardRequestService constructs a ShardRequestService. Panics if sender is nil.
func NewShardRequestService(sender routing.MeshSender) *ShardRequestService {
	if sender == nil {
		panic("vault: sender must not be nil")
	}
	return &ShardRequestService{sender: sender}
}

// RequestShard broadcasts a request for shardHash as a protocol.VaultShardRequest
// (42) packet, stamping requester_uhid with the local node's UHID. Returns the
// number of peers reached. shardHash must not be empty.
func (s *ShardRequestService) RequestShard(ctx context.Context, shardHash string) (int, error) {
	if shardHash == "" {
		return 0, errors.New("vault: shardHash must not be empty")
	}
	body, err := json.Marshal(ShardRequestPayload{
		ShardHash:     shardHash,
		RequesterUhid: s.sender.LocalUhid(),
	})
	if err != nil {
		return 0, err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VaultShardRequest
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	return s.sender.Broadcast(ctx, pkt)
}

// Handle processes an inbound protocol.VaultShardRequest (42) packet: parse it
// and fire OnShardRequested. Returns false (no error) for the wrong packet type,
// a malformed payload, or a request with an empty shard hash. Returns an error
// only if the packet is nil.
func (s *ShardRequestService) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("vault: packet must not be nil")
	}
	if packet.Type != protocol.VaultShardRequest {
		return false, nil
	}

	var body ShardRequestPayload
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.ShardHash == "" {
		return false, nil
	}

	if cb := s.OnShardRequested; cb != nil {
		cb(body.ToRequest())
	}
	return true, nil
}

// SPDX-License-Identifier: MIT

package content

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// DefaultQueryTimeout is the default timeout used by DirectoryService.Resolve
// when no value is supplied.
var DefaultQueryTimeout = 5 * time.Second

// DirectoryService is the application-layer name → ContentDescriptor resolver.
// Closes the Wave-16 protocol gap surfaced by aether-media: an IContentService
// is content-addressed (rootHash-keyed) — consumers that want to fetch content
// by an application-layer name (e.g. "podcast:abc123", "reel:hash",
// "album:artist/title") cannot do so via the content service alone because
// they do not know the rootHash upfront. That's precisely what they're trying
// to discover.
//
// This service maintains a local name catalogue, broadcasts protocol.NamePublish
// when the local node publishes a binding, emits protocol.NameQuery when the
// local node needs to resolve an unknown name, and unicasts a
// protocol.NamePublish response when a peer's query matches an entry we hold.
//
// Added in v1.2.0. Mirrors C#'s AetherNet.Content.IDirectoryService.
type DirectoryService struct {
	sender routing.MeshSender

	mu        sync.RWMutex
	catalogue map[string]ContentDescriptor

	pmu            sync.Mutex
	pendingQueries map[uuid.UUID]chan *ContentDescriptor

	// OnEntryAnnounced fires when a NamePublish packet arrives — either an
	// unsolicited broadcast from a peer or a unicast response to one of our
	// outstanding queries — and updates the local catalogue. Mirrors the C#
	// IDirectoryService.EntryAnnounced event. Set to nil (default) to ignore.
	OnEntryAnnounced func(event *DirectoryEntryAnnouncedEvent)
}

// NewDirectoryService constructs a DirectoryService bound to the given
// MeshSender. Panics if sender is nil — the directory service cannot publish
// or query without a transport.
func NewDirectoryService(sender routing.MeshSender) *DirectoryService {
	if sender == nil {
		panic("content: sender must not be nil")
	}
	return &DirectoryService{
		sender:         sender,
		catalogue:      make(map[string]ContentDescriptor),
		pendingQueries: make(map[uuid.UUID]chan *ContentDescriptor),
	}
}

// Publish stores the binding locally and broadcasts a protocol.NamePublish to
// every connected peer. Subsequent Resolve calls on the local node return the
// descriptor immediately from the catalogue.
func (s *DirectoryService) Publish(ctx context.Context, name string, descriptor ContentDescriptor) error {
	if name == "" {
		return errors.New("content: name must not be empty")
	}

	s.mu.Lock()
	s.catalogue[name] = descriptor
	s.mu.Unlock()

	payload, err := json.Marshal(NamePublishPayload{
		Name:                name,
		Descriptor:          descriptor,
		InResponseToQueryID: nil,
	})
	if err != nil {
		return fmt.Errorf("content: failed to encode NamePublish payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.NamePublish
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = payload

	_, _ = s.sender.Broadcast(ctx, pkt)
	return nil
}

// Resolve returns the descriptor bound to name. The local catalogue is checked
// first; on a hit the descriptor is returned immediately with no network
// activity. On a miss a protocol.NameQuery is broadcast and the call waits up
// to queryTimeout for a matching protocol.NamePublish response. Returns nil
// (without error) on timeout — callers distinguish "not found" from "error"
// via a nil descriptor.
//
// Pass 0 for queryTimeout to use DefaultQueryTimeout (5 seconds).
func (s *DirectoryService) Resolve(ctx context.Context, name string, queryTimeout time.Duration) (*ContentDescriptor, error) {
	if name == "" {
		return nil, errors.New("content: name must not be empty")
	}

	// Local-catalogue hit — return immediately, no network activity.
	s.mu.RLock()
	if cached, ok := s.catalogue[name]; ok {
		s.mu.RUnlock()
		c := cached
		return &c, nil
	}
	s.mu.RUnlock()

	// Set up the pending query waiter BEFORE broadcasting, so an answer that
	// arrives faster than we can register cannot be lost.
	queryID := uuid.New()
	waiter := make(chan *ContentDescriptor, 1)
	s.pmu.Lock()
	s.pendingQueries[queryID] = waiter
	s.pmu.Unlock()
	defer func() {
		s.pmu.Lock()
		delete(s.pendingQueries, queryID)
		s.pmu.Unlock()
	}()

	payload, err := json.Marshal(NameQueryPayload{
		Name:    name,
		QueryID: queryID,
	})
	if err != nil {
		return nil, fmt.Errorf("content: failed to encode NameQuery payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.NameQuery
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = payload

	if _, err := s.sender.Broadcast(ctx, pkt); err != nil {
		return nil, err
	}

	if queryTimeout <= 0 {
		queryTimeout = DefaultQueryTimeout
	}

	timer := time.NewTimer(queryTimeout)
	defer timer.Stop()

	select {
	case desc := <-waiter:
		// May be nil if a race with cleanup happens; treat as a timeout.
		return desc, nil
	case <-timer.C:
		return nil, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

// ListNames returns a snapshot of every name currently in the local catalogue.
func (s *DirectoryService) ListNames(ctx context.Context) ([]string, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	names := make([]string, 0, len(s.catalogue))
	for k := range s.catalogue {
		names = append(names, k)
	}
	return names, nil
}

// Handle pumps inbound protocol.NamePublish / protocol.NameQuery packets into
// the service. Hosts wire this from their transport's receive pump.
func (s *DirectoryService) Handle(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("content: packet must not be nil")
	}
	switch packet.Type {
	case protocol.NamePublish:
		return s.handlePublish(packet)
	case protocol.NameQuery:
		return s.handleQuery(ctx, packet)
	}
	// Silently ignore other packet types — matches C# DirectoryService.HandleAsync.
	return nil
}

func (s *DirectoryService) handlePublish(packet *protocol.MeshPacket) error {
	var payload NamePublishPayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		return fmt.Errorf("content: failed to deserialize NamePublish payload: %w", err)
	}
	if payload.Name == "" {
		return nil
	}

	s.mu.Lock()
	s.catalogue[payload.Name] = payload.Descriptor
	s.mu.Unlock()

	// Query-response correlation: if the payload references a pending query,
	// complete it.
	if payload.InResponseToQueryID != nil {
		s.pmu.Lock()
		if waiter, ok := s.pendingQueries[*payload.InResponseToQueryID]; ok {
			delete(s.pendingQueries, *payload.InResponseToQueryID)
			d := payload.Descriptor
			select {
			case waiter <- &d:
			default:
			}
		}
		s.pmu.Unlock()
	}

	if cb := s.OnEntryAnnounced; cb != nil {
		cb(&DirectoryEntryAnnouncedEvent{
			Name:           payload.Name,
			Descriptor:     payload.Descriptor,
			SourceUhid:     packet.SourceUhid,
			AnnouncedAtUtc: time.Now().UTC(),
		})
	}
	return nil
}

func (s *DirectoryService) handleQuery(ctx context.Context, packet *protocol.MeshPacket) error {
	var query NameQueryPayload
	if err := json.Unmarshal(packet.Payload, &query); err != nil {
		return fmt.Errorf("content: failed to deserialize NameQuery payload: %w", err)
	}
	if query.Name == "" {
		return nil
	}

	s.mu.RLock()
	descriptor, ok := s.catalogue[query.Name]
	s.mu.RUnlock()
	if !ok {
		// We don't hold this name — silently ignore. Other peers may answer.
		return nil
	}

	queryID := query.QueryID
	response := NamePublishPayload{
		Name:                query.Name,
		Descriptor:          descriptor,
		InResponseToQueryID: &queryID,
	}
	body, err := json.Marshal(response)
	if err != nil {
		return fmt.Errorf("content: failed to encode NamePublish response: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.NamePublish
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = packet.SourceUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	_, _ = s.sender.Send(ctx, pkt, packet.SourceUhid)
	return nil
}

// SPDX-License-Identifier: MIT

package routing

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/extensibility"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/reputation"
)

// Service is the AODV-inspired reactive routing service.
//
// Lifecycle:
//   - Callers invoke FindRoute when they need a route to a destination.
//     If the route is in cache it returns immediately; otherwise an RREQ is
//     broadcast and the call awaits the matching RREP (subject to RouteTimeoutMs).
//   - The host pumps incoming RREQs and RREPs into HandleRouteRequest /
//     HandleRouteReply respectively.
//   - The host periodically calls Prune to clear expired routes and trim the
//     RREQ deduplication cache.
type Service struct {
	sender     MeshSender
	store      RouteStore
	verifier   RouteReplyVerifier
	incentives extensibility.IncentiveProvider
	reputation *reputation.NodeReputationService // optional; nil = disabled

	mu           sync.Mutex
	routeCache   map[string]*models.RouteEntry
	pending      map[string]chan *models.RouteEntry
	seenRreqs    map[uuid.UUID]struct{}
	rreqSources  map[string][]int64 // per-source Unix timestamps for rate limiting
	loaded       bool
}

// NewService constructs a Service with the given dependencies. Pass nil for
// store / incentives to get the in-memory / no-op defaults.
//
// A nil verifier is fail-closed: it defaults to RejectAllRouteReplyVerifier, so
// an unconfigured node REJECTS every RREP rather than trusting unverified route
// replies (which would let any forwarder hijack routes). A host wires a real
// signature verifier (Ed25519RouteReplyVerifier) to permit legitimate, signed
// RREPs; tests that exercise routing mechanics pass AcceptAllRouteReplyVerifier
// explicitly to opt out of verification.
func NewService(sender MeshSender, store RouteStore, verifier RouteReplyVerifier, incentives extensibility.IncentiveProvider) *Service {
	if sender == nil {
		panic("routing: sender must not be nil")
	}
	if store == nil {
		store = NewInMemoryRouteStore()
	}
	if verifier == nil {
		verifier = RejectAllRouteReplyVerifier{}
	}
	if incentives == nil {
		incentives = extensibility.NoopIncentiveProvider{}
	}
	return &Service{
		sender:      sender,
		store:       store,
		verifier:    verifier,
		incentives:  incentives,
		routeCache:  make(map[string]*models.RouteEntry),
		pending:     make(map[string]chan *models.RouteEntry),
		seenRreqs:   make(map[uuid.UUID]struct{}),
		rreqSources: make(map[string][]int64),
	}
}

// SetReputation attaches an optional NodeReputationService. Pass nil to disable.
func (s *Service) SetReputation(r *reputation.NodeReputationService) {
	s.mu.Lock()
	s.reputation = r
	s.mu.Unlock()
}

// FindRoute returns a route to destinationUhid, discovering one via RREQ/RREP if
// necessary. Returns nil if no route was found within RouteTimeoutMs.
func (s *Service) FindRoute(ctx context.Context, destinationUhid string) (*models.RouteEntry, error) {
	if destinationUhid == "" {
		return nil, errors.New("routing: destinationUhid must not be empty")
	}

	if err := s.ensureLoaded(ctx); err != nil {
		return nil, err
	}

	s.mu.Lock()
	if cached, ok := s.routeCache[destinationUhid]; ok && !cached.IsStale() {
		s.mu.Unlock()
		return cached, nil
	}
	s.mu.Unlock()

	stored, err := s.store.Get(ctx, destinationUhid)
	if err == nil && stored != nil && !stored.IsStale() {
		s.mu.Lock()
		s.routeCache[destinationUhid] = stored
		s.mu.Unlock()
		return stored, nil
	}

	return s.discover(ctx, destinationUhid)
}

// GetCachedRoute returns the cached route, if any, without triggering discovery.
func (s *Service) GetCachedRoute(destinationUhid string) *models.RouteEntry {
	if destinationUhid == "" {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.routeCache[destinationUhid]
	if !ok || r.IsStale() {
		return nil
	}
	return r
}

// GetAllRoutes returns every non-expired route in the cache.
func (s *Service) GetAllRoutes() []models.RouteEntry {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]models.RouteEntry, 0, len(s.routeCache))
	for _, r := range s.routeCache {
		if !r.IsStale() {
			out = append(out, *r)
		}
	}
	return out
}

// HandleRouteRequest processes an incoming RREQ: installs a reverse route,
// replies if we are the destination, otherwise forwards (decrementing TTL).
func (s *Service) HandleRouteRequest(ctx context.Context, rreq *protocol.MeshPacket) error {
	if rreq == nil {
		return errors.New("routing: rreq must not be nil")
	}
	if rreq.Type != protocol.RouteRequest {
		return errors.New("routing: HandleRouteRequest expected PacketType.RouteRequest")
	}

	s.mu.Lock()
	if _, seen := s.seenRreqs[rreq.ID]; seen {
		s.mu.Unlock()
		return nil
	}
	// Per-source RREQ rate limiting.
	// Only novel packet IDs count against the limit; duplicates already caught
	// above are free so that legitimate multi-path re-transmissions are not
	// penalised.  An attacker sending unique IDs is capped at
	// RreqRateLimitMax per RreqRateLimitWindowSeconds seconds.
	{
		now := time.Now().Unix()
		windowStart := now - int64(constants.RreqRateLimitWindowSeconds)
		var recent []int64
		for _, ts := range s.rreqSources[rreq.SourceUhid] {
			if ts > windowStart {
				recent = append(recent, ts)
			}
		}
		if int32(len(recent)) >= constants.RreqRateLimitMax {
			s.rreqSources[rreq.SourceUhid] = recent
			rep := s.reputation
			s.mu.Unlock()
			if rep != nil {
				rep.RecordRreqFloodAttempt(rreq.SourceUhid)
			}
			return nil // silently drop: source is flooding unique RREQs
		}
		s.rreqSources[rreq.SourceUhid] = append(recent, now)
	}
	s.seenRreqs[rreq.ID] = struct{}{}
	s.mu.Unlock()

	localUhid := s.sender.LocalUhid()
	if rreq.SourceUhid == "" || rreq.SourceUhid == localUhid {
		return nil
	}

	hopCount := int32(constants.DefaultTtl - rreq.Ttl + 1)
	if hopCount < 1 {
		hopCount = 1
	}
	reverse := &models.RouteEntry{
		DestinationUhid: rreq.SourceUhid,
		NextHop:         rreq.SourceUhid,
		HopCount:        hopCount,
		QualityScore:    50,
		ExpiresAt:       time.Now().Add(time.Duration(constants.RouteExpirySeconds) * time.Second),
		SourceUhid:      localUhid,
	}
	s.mu.Lock()
	s.routeCache[reverse.DestinationUhid] = reverse
	s.mu.Unlock()
	if err := s.store.Save(ctx, reverse); err != nil {
		// Cache update is enough to keep the protocol working; surface the error but don't fail.
		_ = err
	}

	if rreq.DestinationUhid == localUhid {
		return s.sendRouteReply(ctx, localUhid, rreq)
	}

	if known, ok := s.cachedRoute(rreq.DestinationUhid); ok {
		_ = known
		return s.sendRouteReply(ctx, rreq.DestinationUhid, rreq)
	}

	if rreq.Ttl > 1 {
		rreq.Ttl--
		fanout, _ := s.sender.Broadcast(ctx, rreq)
		_ = fanout
		_ = s.incentives.RecordRelay(ctx, localUhid, rreq)
	}
	return nil
}

// HandleRouteReply processes an incoming RREP: installs the forward route,
// completes any pending FindRoute, otherwise forwards along the reverse route.
func (s *Service) HandleRouteReply(ctx context.Context, rrep *protocol.MeshPacket) error {
	if rrep == nil {
		return errors.New("routing: rrep must not be nil")
	}
	if rrep.Type != protocol.RouteReply {
		return errors.New("routing: HandleRouteReply expected PacketType.RouteReply")
	}

	ok, err := s.verifier.Verify(ctx, rrep)
	if err != nil {
		return err
	}
	if !ok {
		return nil
	}

	localUhid := s.sender.LocalUhid()
	if rrep.SourceUhid == "" || rrep.SourceUhid == localUhid {
		return nil
	}

	hopCount := int32(constants.DefaultTtl - rrep.Ttl + 1)
	if hopCount < 1 {
		hopCount = 1
	}
	forward := &models.RouteEntry{
		DestinationUhid: rrep.SourceUhid,
		NextHop:         rrep.SourceUhid,
		HopCount:        hopCount,
		QualityScore:    50,
		ExpiresAt:       time.Now().Add(time.Duration(constants.RouteExpirySeconds) * time.Second),
		SourceUhid:      localUhid,
	}
	s.mu.Lock()
	s.routeCache[forward.DestinationUhid] = forward
	if ch, waiting := s.pending[forward.DestinationUhid]; waiting && rrep.DestinationUhid == localUhid {
		delete(s.pending, forward.DestinationUhid)
		// Non-blocking send: closed/full channels are tolerated.
		select {
		case ch <- forward:
		default:
		}
	}
	s.mu.Unlock()
	_ = s.store.Save(ctx, forward)

	if rrep.DestinationUhid == localUhid {
		return nil
	}

	if rrep.Ttl <= 1 {
		return nil
	}

	if next, ok := s.cachedRoute(rrep.DestinationUhid); ok && !next.IsStale() {
		rrep.Ttl--
		delivered, _ := s.sender.Send(ctx, rrep, next.NextHop)
		if delivered {
			_ = s.incentives.RecordRelay(ctx, localUhid, rrep)
		}
	}
	return nil
}

// Prune removes expired routes and trims the RREQ dedup state.
func (s *Service) Prune(ctx context.Context) error {
	s.mu.Lock()
	now := time.Now()
	for k, r := range s.routeCache {
		if now.After(r.ExpiresAt) {
			delete(s.routeCache, k)
		}
	}
	if len(s.seenRreqs) > 10_000 {
		s.seenRreqs = make(map[uuid.UUID]struct{})
	}
	// Prune stale per-source rate-limit entries.
	oldWindow := time.Now().Unix() - int64(constants.RreqRateLimitWindowSeconds)
	for src, timestamps := range s.rreqSources {
		var kept []int64
		for _, ts := range timestamps {
			if ts > oldWindow {
				kept = append(kept, ts)
			}
		}
		if len(kept) == 0 {
			delete(s.rreqSources, src)
		} else {
			s.rreqSources[src] = kept
		}
	}
	s.mu.Unlock()

	_, err := s.store.PruneExpired(ctx)
	return err
}

func (s *Service) cachedRoute(uhid string) (*models.RouteEntry, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.routeCache[uhid]
	if !ok || r.IsStale() {
		return nil, false
	}
	return r, true
}

func (s *Service) sendRouteReply(ctx context.Context, repliedSource string, rreq *protocol.MeshPacket) error {
	rrep := protocol.NewMeshPacket()
	rrep.Type = protocol.RouteReply
	rrep.SourceUhid = repliedSource
	rrep.DestinationUhid = rreq.SourceUhid
	rrep.Ttl = constants.DefaultTtl
	rrep.Payload = rreq.Payload

	if reverse, ok := s.cachedRoute(rreq.SourceUhid); ok {
		_, err := s.sender.Send(ctx, rrep, reverse.NextHop)
		return err
	}
	_, err := s.sender.Broadcast(ctx, rrep)
	return err
}

func (s *Service) discover(ctx context.Context, destinationUhid string) (*models.RouteEntry, error) {
	ch := make(chan *models.RouteEntry, 1)
	s.mu.Lock()
	s.pending[destinationUhid] = ch
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		delete(s.pending, destinationUhid)
		s.mu.Unlock()
	}()

	rreq := protocol.NewMeshPacket()
	rreq.Type = protocol.RouteRequest
	rreq.SourceUhid = s.sender.LocalUhid()
	rreq.DestinationUhid = destinationUhid
	rreq.Ttl = constants.DefaultTtl

	fanout, err := s.sender.Broadcast(ctx, rreq)
	if err != nil {
		return nil, err
	}
	if fanout == 0 {
		return nil, nil
	}

	timeout := time.NewTimer(time.Duration(constants.RouteTimeoutMs) * time.Millisecond)
	defer timeout.Stop()

	select {
	case route := <-ch:
		return route, nil
	case <-timeout.C:
		return nil, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (s *Service) ensureLoaded(ctx context.Context) error {
	s.mu.Lock()
	if s.loaded {
		s.mu.Unlock()
		return nil
	}
	s.loaded = true
	s.mu.Unlock()

	all, err := s.store.GetAll(ctx)
	if err != nil {
		s.mu.Lock()
		s.loaded = false
		s.mu.Unlock()
		return err
	}
	s.mu.Lock()
	for i := range all {
		if !all[i].IsStale() {
			r := all[i]
			s.routeCache[r.DestinationUhid] = &r
		}
	}
	s.mu.Unlock()
	return nil
}

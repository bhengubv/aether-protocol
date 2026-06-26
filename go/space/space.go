// SPDX-License-Identifier: MIT

// Package space implements aether-space: geo-pinned community noticeboards
// (Phase-2 extension). Nodes drop breadcrumbs at geohash coordinates; passing
// devices auto-pull and re-host them for other passersby — fully offline.
//
// Port of the C# reference (AetherNet.Space). Wire format: JSON, transmitted as
// PacketType.SpaceBreadcrumb (40).
package space

import (
	"context"
	"strings"
	"sync"
	"time"
)

// BreadcrumbType is the category of a geo-pinned breadcrumb.
type BreadcrumbType uint8

const (
	// BreadcrumbTypeNotice is a general community notice (default).
	BreadcrumbTypeNotice BreadcrumbType = 0
	// BreadcrumbTypeEmergency bypasses the flood-guard; TTL extended to 720 h.
	BreadcrumbTypeEmergency BreadcrumbType = 1
	// BreadcrumbTypeCommerce is a commercial listing or market offer.
	BreadcrumbTypeCommerce BreadcrumbType = 2
	// BreadcrumbTypeEvent is a local event announcement.
	BreadcrumbTypeEvent BreadcrumbType = 3
	// BreadcrumbTypeJobPosting is a job posting or opportunity.
	BreadcrumbTypeJobPosting BreadcrumbType = 4
)

// EmergencyTtlHours is the fixed TTL applied to Emergency breadcrumbs.
const EmergencyTtlHours = 720

// MinTtlHours / MaxTtlHours bound a non-emergency breadcrumb's lifetime.
const (
	MinTtlHours = 1
	MaxTtlHours = 168
)

// Breadcrumb is a geo-pinned digital notice dropped by a user at a physical
// location. Content is addressed by hash; the breadcrumb carries only metadata.
type Breadcrumb struct {
	ContentHash string // content-service hash of the actual payload
	GeoHash     string // 6-character geohash of the drop location (~1.2 km² cell)
	AnchorUhid  string // UHID of the node that dropped the breadcrumb
	CreatedAt   time.Time
	TtlHours    int
	Type        BreadcrumbType
	Signature   []byte // Ed25519 over (ContentHash + GeoHash + CreatedAt ISO-8601); empty if unsigned
}

// ExpiresAt is CreatedAt + TtlHours.
func (b *Breadcrumb) ExpiresAt() time.Time {
	return b.CreatedAt.Add(time.Duration(b.TtlHours) * time.Hour)
}

// IsExpired reports whether the breadcrumb's TTL has passed.
func (b *Breadcrumb) IsExpired() bool {
	return !time.Now().UTC().Before(b.ExpiresAt())
}

// Service is the aether-space breadcrumb store.
type Service interface {
	// Drop creates a new breadcrumb at geoHash. ttlHours is clamped to
	// [1,168]; Emergency breadcrumbs are fixed at 720 h.
	Drop(ctx context.Context, geoHash, contentHash, anchorUhid string, typ BreadcrumbType, ttlHours int) (*Breadcrumb, error)
	// Scan returns active (non-expired) breadcrumbs near centerGeoHash.
	Scan(ctx context.Context, centerGeoHash string, radiusCells int) ([]Breadcrumb, error)
	// Pin caches and re-hosts a breadcrumb received from a peer.
	Pin(ctx context.Context, breadcrumb *Breadcrumb) error
	// Delete removes a breadcrumb; succeeds only if requestorUhid is the
	// breadcrumb's AnchorUhid (creator-only delete).
	Delete(ctx context.Context, breadcrumb *Breadcrumb, requestorUhid string) (bool, error)
	// PruneExpired drops every expired breadcrumb and returns the count removed.
	PruneExpired() int
}

// InMemoryService is an in-memory Service for testing and single-node use; all
// state is lost on restart. Proximity matching uses a geohash-prefix heuristic.
//
// Not safe to mutate the callbacks concurrently with calls; set them before use.
type InMemoryService struct {
	mu    sync.Mutex
	store map[string]*Breadcrumb // key = ContentHash

	// OnBreadcrumbReceived fires when a breadcrumb is dropped locally or pinned
	// from the mesh. OnBreadcrumbExpired fires when a cached breadcrumb is pruned.
	OnBreadcrumbReceived func(*Breadcrumb)
	OnBreadcrumbExpired  func(*Breadcrumb)
}

// NewInMemoryService constructs an empty in-memory space service.
func NewInMemoryService() *InMemoryService {
	return &InMemoryService{store: make(map[string]*Breadcrumb)}
}

func clampInt(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// Drop implements Service.
func (s *InMemoryService) Drop(ctx context.Context, geoHash, contentHash, anchorUhid string, typ BreadcrumbType, ttlHours int) (*Breadcrumb, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	effectiveTtl := clampInt(ttlHours, MinTtlHours, MaxTtlHours)
	if typ == BreadcrumbTypeEmergency {
		effectiveTtl = EmergencyTtlHours
	}
	crumb := &Breadcrumb{
		ContentHash: contentHash,
		GeoHash:     geoHash,
		AnchorUhid:  anchorUhid,
		CreatedAt:   time.Now().UTC(),
		TtlHours:    effectiveTtl,
		Type:        typ,
	}
	s.mu.Lock()
	s.store[contentHash] = crumb
	s.mu.Unlock()
	if s.OnBreadcrumbReceived != nil {
		s.OnBreadcrumbReceived(crumb)
	}
	return crumb, nil
}

// Scan implements Service.
func (s *InMemoryService) Scan(ctx context.Context, centerGeoHash string, radiusCells int) ([]Breadcrumb, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	// Prefix-based proximity: match the first (6 - radiusCells) chars.
	prefixLen := clampInt(6-radiusCells, 1, 6)
	prefix := centerGeoHash
	if len(centerGeoHash) >= prefixLen {
		prefix = centerGeoHash[:prefixLen]
	}
	lowerPrefix := strings.ToLower(prefix)

	s.mu.Lock()
	defer s.mu.Unlock()
	results := make([]Breadcrumb, 0)
	for _, c := range s.store {
		if !c.IsExpired() && strings.HasPrefix(strings.ToLower(c.GeoHash), lowerPrefix) {
			results = append(results, *c)
		}
	}
	return results, nil
}

// Pin implements Service.
func (s *InMemoryService) Pin(ctx context.Context, breadcrumb *Breadcrumb) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	s.mu.Lock()
	s.store[breadcrumb.ContentHash] = breadcrumb
	s.mu.Unlock()
	if s.OnBreadcrumbReceived != nil {
		s.OnBreadcrumbReceived(breadcrumb)
	}
	return nil
}

// Delete implements Service.
func (s *InMemoryService) Delete(ctx context.Context, breadcrumb *Breadcrumb, requestorUhid string) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	stored, ok := s.store[breadcrumb.ContentHash]
	if !ok {
		return false, nil
	}
	if stored.AnchorUhid != requestorUhid {
		return false, nil // creator-only delete
	}
	delete(s.store, breadcrumb.ContentHash)
	return true, nil
}

// PruneExpired implements Service.
func (s *InMemoryService) PruneExpired() int {
	s.mu.Lock()
	expired := make([]*Breadcrumb, 0)
	for _, c := range s.store {
		if c.IsExpired() {
			expired = append(expired, c)
		}
	}
	for _, c := range expired {
		delete(s.store, c.ContentHash)
	}
	s.mu.Unlock()

	for _, c := range expired {
		if s.OnBreadcrumbExpired != nil {
			s.OnBreadcrumbExpired(c)
		}
	}
	return len(expired)
}

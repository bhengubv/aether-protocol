// SPDX-License-Identifier: MIT

// Package forge implements aether-forge: a mesh-native package cache proxy
// (Phase-2 extension). The first internet pull of a package is cached as Aether
// content; subsequent pulls by anyone in the mesh are served locally at mesh
// speeds. Port of the C# reference (AetherNet.Forge). Ecosystems: npm, pip,
// cargo, go, nuget, git.
package forge

import (
	"context"
	"sort"
	"sync"
	"time"
)

// Entry is the metadata record for one cached package artifact. Package IDs use
// a namespaced "ecosystem:name@version" format (e.g. "npm:react@18.2.0").
type Entry struct {
	ContentHash   string
	PackageID     string
	FetchedAt     time.Time
	SizeBytes     int64
	DownloadCount int
}

// Stats are aggregate statistics for the local Forge cache.
type Stats struct {
	TotalBytesSaved  int64
	TotalPeersServed int
	CatalogueSize    int
	TopPackages      []Entry // most-downloaded first, up to 10
}

// Service is the mesh-native package cache.
type Service interface {
	// Query looks up a cached entry by package ID; returns nil if not cached.
	Query(ctx context.Context, packageID string) (*Entry, error)
	// Cache stores a new artifact (idempotent — first write wins).
	Cache(ctx context.Context, packageID, contentHash string, sizeBytes int64) (*Entry, error)
	// Fetch increments the download counter and returns the entry, or nil if not cached.
	Fetch(ctx context.Context, packageID string) (*Entry, error)
	// GetStats returns current aggregate cache statistics.
	GetStats(ctx context.Context) (Stats, error)
}

// InMemoryService is an in-memory Service for testing / single-node use; state
// is lost on restart.
type InMemoryService struct {
	mu    sync.Mutex
	store map[string]*Entry // key = packageID

	// OnNewEntryAnnounced fires when a new artifact is added via Cache.
	OnNewEntryAnnounced func(*Entry)
}

// NewInMemoryService constructs an empty in-memory forge service.
func NewInMemoryService() *InMemoryService {
	return &InMemoryService{store: make(map[string]*Entry)}
}

// Query implements Service.
func (s *InMemoryService) Query(ctx context.Context, packageID string) (*Entry, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	e, ok := s.store[packageID]
	if !ok {
		return nil, nil
	}
	cp := *e
	return &cp, nil
}

// Cache implements Service (idempotent: an existing packageID is returned unchanged).
func (s *InMemoryService) Cache(ctx context.Context, packageID, contentHash string, sizeBytes int64) (*Entry, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	s.mu.Lock()
	e, ok := s.store[packageID]
	isNew := false
	if !ok {
		e = &Entry{
			PackageID:     packageID,
			ContentHash:   contentHash,
			SizeBytes:     sizeBytes,
			FetchedAt:     time.Now().UTC(),
			DownloadCount: 0,
		}
		s.store[packageID] = e
		isNew = true
	}
	cp := *e
	s.mu.Unlock()

	if isNew && s.OnNewEntryAnnounced != nil {
		s.OnNewEntryAnnounced(&cp)
	}
	return &cp, nil
}

// Fetch implements Service.
func (s *InMemoryService) Fetch(ctx context.Context, packageID string) (*Entry, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	e, ok := s.store[packageID]
	if !ok {
		return nil, nil
	}
	e.DownloadCount++
	cp := *e
	return &cp, nil
}

// GetStats implements Service.
func (s *InMemoryService) GetStats(ctx context.Context) (Stats, error) {
	if err := ctx.Err(); err != nil {
		return Stats{}, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	entries := make([]Entry, 0, len(s.store))
	var totalBytesSaved int64
	for _, e := range s.store {
		entries = append(entries, *e)
		totalBytesSaved += int64(e.DownloadCount) * e.SizeBytes
	}
	sort.SliceStable(entries, func(i, j int) bool {
		return entries[i].DownloadCount > entries[j].DownloadCount
	})
	top := entries
	if len(top) > 10 {
		top = top[:10]
	}
	return Stats{
		TotalBytesSaved:  totalBytesSaved,
		TotalPeersServed: 0, // no peer tracking in the in-memory implementation
		CatalogueSize:    len(s.store),
		TopPackages:      top,
	}, nil
}

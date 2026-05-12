// SPDX-License-Identifier: MIT

// Package dtn implements delay-tolerant networking on top of the Aether mesh.
// Bundles are JSON-encoded into MeshPacket payloads, replicated to nearby peers
// via an epidemic strategy (default: geohash-proximity), and delivered when the
// recipient appears or accepts custody from a closer carrier.
package dtn

import (
	"context"
	"sync"

	"github.com/bhengubv/aether-protocol/go/models"
)

// BundleStore is the persistent backing store for DTN bundles + custody records.
type BundleStore interface {
	Get(ctx context.Context, bundleID string) (*models.DtnBundle, error)
	GetActive(ctx context.Context) ([]models.DtnBundle, error)
	Save(ctx context.Context, bundle *models.DtnBundle) error
	Remove(ctx context.Context, bundleID string) error
	GetActiveCount(ctx context.Context) (int, error)
	SaveCustody(ctx context.Context, record *models.CustodyRecord) error
	GetCustodyRecords(ctx context.Context, bundleID string) ([]models.CustodyRecord, error)
	ExpireStale(ctx context.Context) (int, error)
}

// InMemoryBundleStore is a process-local store. Loses data on restart.
type InMemoryBundleStore struct {
	mu       sync.RWMutex
	bundles  map[string]*models.DtnBundle
	custody  map[string]*models.CustodyRecord
}

// NewInMemoryBundleStore creates a fresh empty store.
func NewInMemoryBundleStore() *InMemoryBundleStore {
	return &InMemoryBundleStore{
		bundles: make(map[string]*models.DtnBundle),
		custody: make(map[string]*models.CustodyRecord),
	}
}

func (s *InMemoryBundleStore) Get(ctx context.Context, bundleID string) (*models.DtnBundle, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	b, ok := s.bundles[bundleID]
	if !ok {
		return nil, nil
	}
	return b, nil
}

func (s *InMemoryBundleStore) GetActive(ctx context.Context) ([]models.DtnBundle, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]models.DtnBundle, 0, len(s.bundles))
	for _, b := range s.bundles {
		if b.IsExpired() {
			continue
		}
		if b.Status == models.DtnStatusPending || b.Status == models.DtnStatusInCustody {
			out = append(out, *b)
		}
	}
	return out, nil
}

func (s *InMemoryBundleStore) Save(ctx context.Context, bundle *models.DtnBundle) error {
	if bundle == nil {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.bundles[bundle.ID] = bundle
	return nil
}

func (s *InMemoryBundleStore) Remove(ctx context.Context, bundleID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.bundles, bundleID)
	return nil
}

func (s *InMemoryBundleStore) GetActiveCount(ctx context.Context) (int, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	count := 0
	for _, b := range s.bundles {
		if b.IsExpired() {
			continue
		}
		if b.Status == models.DtnStatusPending || b.Status == models.DtnStatusInCustody {
			count++
		}
	}
	return count, nil
}

func (s *InMemoryBundleStore) SaveCustody(ctx context.Context, record *models.CustodyRecord) error {
	if record == nil {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.custody[record.ID] = record
	return nil
}

func (s *InMemoryBundleStore) GetCustodyRecords(ctx context.Context, bundleID string) ([]models.CustodyRecord, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]models.CustodyRecord, 0)
	for _, r := range s.custody {
		if r.BundleID == bundleID {
			out = append(out, *r)
		}
	}
	return out, nil
}

func (s *InMemoryBundleStore) ExpireStale(ctx context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	expired := 0
	for _, b := range s.bundles {
		if b.IsExpired() && b.Status != models.DtnStatusExpired {
			b.Status = models.DtnStatusExpired
			expired++
		}
	}
	return expired, nil
}

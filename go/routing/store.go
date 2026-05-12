// SPDX-License-Identifier: MIT

package routing

import (
	"context"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/models"
)

// RouteStore is the persistent backing store for the routing table. The default
// InMemoryRouteStore is process-local; production hosts substitute file- or
// SQLite-backed implementations so routes survive restarts.
type RouteStore interface {
	Get(ctx context.Context, destinationUhid string) (*models.RouteEntry, error)
	GetAll(ctx context.Context) ([]models.RouteEntry, error)
	Save(ctx context.Context, route *models.RouteEntry) error
	Remove(ctx context.Context, destinationUhid string) error
	PruneExpired(ctx context.Context) (int, error)
}

// InMemoryRouteStore is the volatile, process-local default.
type InMemoryRouteStore struct {
	mu     sync.RWMutex
	routes map[string]*models.RouteEntry
}

// NewInMemoryRouteStore creates a fresh empty store.
func NewInMemoryRouteStore() *InMemoryRouteStore {
	return &InMemoryRouteStore{routes: make(map[string]*models.RouteEntry)}
}

func (s *InMemoryRouteStore) Get(ctx context.Context, destinationUhid string) (*models.RouteEntry, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	r, ok := s.routes[destinationUhid]
	if !ok {
		return nil, nil
	}
	return r, nil
}

func (s *InMemoryRouteStore) GetAll(ctx context.Context) ([]models.RouteEntry, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	all := make([]models.RouteEntry, 0, len(s.routes))
	for _, r := range s.routes {
		all = append(all, *r)
	}
	return all, nil
}

func (s *InMemoryRouteStore) Save(ctx context.Context, route *models.RouteEntry) error {
	if route == nil {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.routes[route.DestinationUhid] = route
	return nil
}

func (s *InMemoryRouteStore) Remove(ctx context.Context, destinationUhid string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.routes, destinationUhid)
	return nil
}

func (s *InMemoryRouteStore) PruneExpired(ctx context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	pruned := 0
	now := time.Now()
	for k, r := range s.routes {
		if now.After(r.ExpiresAt) {
			delete(s.routes, k)
			pruned++
		}
	}
	return pruned, nil
}

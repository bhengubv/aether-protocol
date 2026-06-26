// SPDX-License-Identifier: MIT
//
// Offline-capable P2P marketplace (aether-market Phase-2 extension). Go port of
// AetherNet.Market.IMarketService / InMemoryMarketService and the listing/escrow
// models. Listings are geo-pinned (distributed via aether-space) and may carry a
// vault.Manifest escrow for document-backed sales; trades run a two-party confirm
// state machine. Requires aether-space and aether-vault.
package market

import (
	"context"
	"strings"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/vault"
	"github.com/google/uuid"
)

// MarketCategory is the category of a MarketListing.
type MarketCategory byte

const (
	CategoryGoods     MarketCategory = 0
	CategoryServices  MarketCategory = 1
	CategoryLabour    MarketCategory = 2
	CategoryLand      MarketCategory = 3
	CategoryDocuments MarketCategory = 4
)

// TradeRole is the role of the node confirming a trade step.
type TradeRole byte

const (
	RoleBuyer  TradeRole = 0
	RoleSeller TradeRole = 1
)

// TradeState is the state machine for a TradeEscrow.
type TradeState byte

const (
	StateInitiated       TradeState = 0
	StateBuyerConfirmed  TradeState = 1
	StateSellerConfirmed TradeState = 2
	StateComplete        TradeState = 3
	StateDisputed        TradeState = 4
)

// MarketListing is a geo-pinned market listing dropped by a verified seller. It may include a
// vault.Manifest escrow for document-backed sales (land deeds, certificates).
type MarketListing struct {
	ListingID      string          `json:"listing_id"`
	SellerUhid     string          `json:"seller_uhid"`
	SellerPoVScore PoVScore        `json:"seller_pov_score"`
	Title          string          `json:"title"`
	Description    string          `json:"description"`
	PriceZAR       float64         `json:"price_zar"` // South African Rand
	GeoHash        string          `json:"geohash"`   // 6-char geohash of the listing location
	Category       MarketCategory  `json:"category"`
	EscrowManifest *vault.Manifest `json:"escrow_manifest,omitempty"` // optional Vault escrow
	CreatedAt      time.Time       `json:"created_at"`
	ExpiresAt      time.Time       `json:"expires_at"`
}

// IsExpired reports whether the listing has reached its expiry.
func (l *MarketListing) IsExpired() bool { return !time.Now().UTC().Before(l.ExpiresAt) }

// TradeEscrow tracks the lifecycle of a marketplace trade.
type TradeEscrow struct {
	EscrowID      string          `json:"escrow_id"`
	ListingID     string          `json:"listing_id"`
	BuyerUhid     string          `json:"buyer_uhid"`
	SellerUhid    string          `json:"seller_uhid"`
	State         TradeState      `json:"state"`
	VaultManifest *vault.Manifest `json:"vault_manifest,omitempty"`
	CreatedAt     time.Time       `json:"created_at"`
}

// MarketService is the offline-capable P2P marketplace.
type MarketService interface {
	CreateListing(ctx context.Context, sellerUhid, title, description string, priceZAR float64, geoHash string, category MarketCategory) (*MarketListing, error)
	BrowseNearby(ctx context.Context, centerGeoHash string, radiusCells int) ([]*MarketListing, error)
	Search(ctx context.Context, query string, category *MarketCategory) ([]*MarketListing, error)
	InitiateTrade(ctx context.Context, listing *MarketListing, buyerUhid string) (*TradeEscrow, error)
	ConfirmTrade(ctx context.Context, escrow *TradeEscrow, role TradeRole) (*TradeEscrow, error)
	Dispute(ctx context.Context, escrow *TradeEscrow, reason string) error
}

// InMemoryMarketService is an in-memory MarketService for testing / single-node use.
type InMemoryMarketService struct {
	mu       sync.Mutex
	listings map[string]*MarketListing
	escrows  map[string]*TradeEscrow

	// OnListingReceived fires when a new listing is received from the mesh or created locally.
	OnListingReceived func(*MarketListing)
}

// NewInMemoryMarketService constructs an empty in-memory market service.
func NewInMemoryMarketService() *InMemoryMarketService {
	return &InMemoryMarketService{
		listings: make(map[string]*MarketListing),
		escrows:  make(map[string]*TradeEscrow),
	}
}

// CreateListing creates and stores a new listing, then fires OnListingReceived.
func (s *InMemoryMarketService) CreateListing(ctx context.Context, sellerUhid, title, description string, priceZAR float64, geoHash string, category MarketCategory) (*MarketListing, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	now := time.Now().UTC()
	listing := &MarketListing{
		ListingID:   uuid.NewString(),
		SellerUhid:  sellerUhid,
		Title:       title,
		Description: description,
		PriceZAR:    priceZAR,
		GeoHash:     geoHash,
		Category:    category,
		CreatedAt:   now,
		ExpiresAt:   now.AddDate(0, 0, 30),
	}
	s.mu.Lock()
	s.listings[listing.ListingID] = listing
	s.mu.Unlock()

	if cb := s.OnListingReceived; cb != nil {
		cb(listing)
	}
	return listing, nil
}

// BrowseNearby returns non-expired listings whose geohash shares the center prefix (length
// = len(center) - radiusCells + 1, floored at 1).
func (s *InMemoryMarketService) BrowseNearby(ctx context.Context, centerGeoHash string, radiusCells int) ([]*MarketListing, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	prefixLen := len(centerGeoHash) - radiusCells + 1
	if prefixLen < 1 {
		prefixLen = 1
	}
	if prefixLen > len(centerGeoHash) {
		prefixLen = len(centerGeoHash)
	}
	prefix := strings.ToLower(centerGeoHash[:prefixLen])

	s.mu.Lock()
	defer s.mu.Unlock()
	results := make([]*MarketListing, 0)
	for _, l := range s.listings {
		if !l.IsExpired() && strings.HasPrefix(strings.ToLower(l.GeoHash), prefix) {
			results = append(results, l)
		}
	}
	return results, nil
}

// Search returns non-expired listings whose title or description contains query (case-insensitive),
// optionally filtered by category.
func (s *InMemoryMarketService) Search(ctx context.Context, query string, category *MarketCategory) ([]*MarketListing, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	q := strings.ToLower(query)

	s.mu.Lock()
	defer s.mu.Unlock()
	results := make([]*MarketListing, 0)
	for _, l := range s.listings {
		if l.IsExpired() {
			continue
		}
		if category != nil && l.Category != *category {
			continue
		}
		if strings.Contains(strings.ToLower(l.Title), q) || strings.Contains(strings.ToLower(l.Description), q) {
			results = append(results, l)
		}
	}
	return results, nil
}

// InitiateTrade opens an escrow in the Initiated state for listing/buyer.
func (s *InMemoryMarketService) InitiateTrade(ctx context.Context, listing *MarketListing, buyerUhid string) (*TradeEscrow, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	escrow := &TradeEscrow{
		EscrowID:      uuid.NewString(),
		ListingID:     listing.ListingID,
		BuyerUhid:     buyerUhid,
		SellerUhid:    listing.SellerUhid,
		State:         StateInitiated,
		VaultManifest: listing.EscrowManifest,
		CreatedAt:     time.Now().UTC(),
	}
	s.mu.Lock()
	s.escrows[escrow.EscrowID] = escrow
	s.mu.Unlock()
	return escrow, nil
}

// ConfirmTrade advances the escrow state machine. Buyer → BuyerConfirmed; Seller → Complete if the
// buyer already confirmed, else SellerConfirmed.
func (s *InMemoryMarketService) ConfirmTrade(ctx context.Context, escrow *TradeEscrow, role TradeRole) (*TradeEscrow, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if role == RoleBuyer {
		escrow.State = StateBuyerConfirmed
	} else {
		if escrow.State == StateBuyerConfirmed {
			escrow.State = StateComplete
		} else {
			escrow.State = StateSellerConfirmed
		}
	}
	s.mu.Lock()
	s.escrows[escrow.EscrowID] = escrow
	s.mu.Unlock()
	return escrow, nil
}

// Dispute marks the escrow Disputed.
func (s *InMemoryMarketService) Dispute(ctx context.Context, escrow *TradeEscrow, reason string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	escrow.State = StateDisputed
	s.mu.Lock()
	s.escrows[escrow.EscrowID] = escrow
	s.mu.Unlock()
	return nil
}

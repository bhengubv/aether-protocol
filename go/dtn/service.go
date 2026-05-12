// SPDX-License-Identifier: MIT

package dtn

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/extensibility"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/reputation"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// Service is the default DTN service. Bundles are JSON-encoded into the payload
// of MeshPacket(DtnBundle); custody acks and delivery receipts use their own
// PacketTypes with small JSON envelopes.
type Service struct {
	sender     routing.MeshSender
	store      BundleStore
	strategy   ReplicationStrategy
	incentives extensibility.IncentiveProvider
	backend    extensibility.BackendClient
	reputation *reputation.NodeReputationService

	mu sync.Mutex

	OnBundleDelivered func(receipt *models.DtnDeliveryReceipt)
}

// SetReputation attaches an optional NodeReputationService to the DTN service.
// It is safe to call after construction. Pass nil to detach.
func (s *Service) SetReputation(r *reputation.NodeReputationService) {
	s.mu.Lock()
	s.reputation = r
	s.mu.Unlock()
}

// NewService constructs a Service. Pass nil for any optional dependency to
// receive the package-default no-op / in-memory implementation.
func NewService(sender routing.MeshSender, store BundleStore, strategy ReplicationStrategy, incentives extensibility.IncentiveProvider, backend extensibility.BackendClient) *Service {
	if sender == nil {
		panic("dtn: sender must not be nil")
	}
	if store == nil {
		store = NewInMemoryBundleStore()
	}
	if strategy == nil {
		strategy = GeohashEpidemicStrategy{}
	}
	if incentives == nil {
		incentives = extensibility.NoopIncentiveProvider{}
	}
	if backend == nil {
		backend = extensibility.NoopBackendClient{}
	}
	return &Service{
		sender:     sender,
		store:      store,
		strategy:   strategy,
		incentives: incentives,
		backend:    backend,
	}
}

// CreateBundle creates and queues a new bundle. Attempts immediate mesh delivery;
// falls back to backend relay; on failure stays in the store for the next scan.
func (s *Service) CreateBundle(ctx context.Context, recipientUhid string, encryptedPayload []byte, priority models.DtnPriority, recipientLastGeohash string) (*models.DtnBundle, error) {
	if recipientUhid == "" {
		return nil, errors.New("dtn: recipientUhid must not be empty")
	}

	bundle := &models.DtnBundle{
		ID:                   uuid.NewString(),
		SenderUhid:           s.sender.LocalUhid(),
		RecipientUhid:        recipientUhid,
		EncryptedPayload:     encryptedPayload,
		Priority:             priority,
		Status:               models.DtnStatusPending,
		CopyCount:            1,
		MaxCopies:            constants.DtnMaxCopies,
		SenderGeohash:        s.sender.LocalGeohash(),
		RecipientLastGeohash: recipientLastGeohash,
		CreatedAt:            time.Now(),
		ExpiresAt:            time.Now().Add(time.Duration(constants.DtnBundleTtlHours) * time.Hour),
	}
	if err := s.store.Save(ctx, bundle); err != nil {
		return nil, err
	}

	if delivered, _ := s.tryDirectDelivery(ctx, bundle); delivered {
		bundle.Status = models.DtnStatusDelivered
		_ = s.store.Save(ctx, bundle)
	}
	return bundle, nil
}

// Handle pumps a received DTN packet into the service.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("dtn: packet must not be nil")
	}
	switch packet.Type {
	case protocol.DtnBundle:
		return s.handleBundle(ctx, packet)
	case protocol.DtnCustodyAck:
		return s.handleCustodyAck(ctx, packet)
	case protocol.DtnDeliveryReceipt:
		return s.handleDeliveryReceipt(ctx, packet)
	}
	return nil
}

// RunDeliveryScan retries every active bundle: tries direct delivery first,
// then replicates to chosen peers.
func (s *Service) RunDeliveryScan(ctx context.Context) error {
	active, err := s.store.GetActive(ctx)
	if err != nil {
		return err
	}
	if len(active) == 0 {
		return nil
	}
	peers := s.sender.ConnectedPeers()

	for i := range active {
		bundle := &active[i]
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if bundle.Status == models.DtnStatusDelivered || bundle.IsExpired() {
			continue
		}

		if delivered, _ := s.tryDirectDelivery(ctx, bundle); delivered {
			bundle.Status = models.DtnStatusDelivered
			_ = s.store.Save(ctx, bundle)
			continue
		}

		if len(peers) == 0 || bundle.CopyCount >= bundle.MaxCopies {
			continue
		}
		targets := s.strategy.SelectTargets(bundle, peers, s.sender.LocalGeohash())
		for _, target := range targets {
			if ctx.Err() != nil {
				break
			}
			if bundle.CopyCount >= bundle.MaxCopies {
				break
			}
			pkt, err := s.bundlePacket(bundle, target)
			if err != nil {
				continue
			}
			ok, _ := s.sender.Send(ctx, pkt, target)
			if ok {
				bundle.CopyCount++
				_ = s.store.Save(ctx, bundle)
				_ = s.incentives.RecordRelay(ctx, s.sender.LocalUhid(), pkt)
			}
		}
	}
	return nil
}

// ExpireStale marks every expired bundle as Expired in the store.
func (s *Service) ExpireStale(ctx context.Context) (int, error) {
	return s.store.ExpireStale(ctx)
}

// GetActiveBundles returns every bundle currently held in active state.
func (s *Service) GetActiveBundles(ctx context.Context) ([]models.DtnBundle, error) {
	return s.store.GetActive(ctx)
}

func (s *Service) tryDirectDelivery(ctx context.Context, bundle *models.DtnBundle) (bool, error) {
	pkt, err := s.bundlePacket(bundle, bundle.RecipientUhid)
	if err != nil {
		return false, err
	}

	for _, p := range s.sender.ConnectedPeers() {
		if p.UHID == bundle.RecipientUhid {
			ok, _ := s.sender.Send(ctx, pkt, bundle.RecipientUhid)
			if ok {
				return true, nil
			}
			break
		}
	}

	bundleJSON, err := json.Marshal(snakeCaseBundle(bundle))
	if err != nil {
		return false, err
	}
	ok, _ := s.backend.SyncDtnBundle(ctx, bundleJSON)
	return ok, nil
}

func (s *Service) bundlePacket(bundle *models.DtnBundle, nextHopUhid string) (*protocol.MeshPacket, error) {
	body, err := json.Marshal(snakeCaseBundle(bundle))
	if err != nil {
		return nil, err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnBundle
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = bundle.RecipientUhid
	pkt.Ttl = 30 // DtnTtl per spec
	if int(bundle.Priority) > 255 {
		pkt.Priority = 255
	} else {
		pkt.Priority = byte(bundle.Priority)
	}
	pkt.Payload = body
	return pkt, nil
}

func (s *Service) handleBundle(ctx context.Context, packet *protocol.MeshPacket) error {
	var wire bundleWire
	if err := json.Unmarshal(packet.Payload, &wire); err != nil {
		return fmt.Errorf("dtn: failed to deserialize bundle: %w", err)
	}
	bundle := wire.toBundle()

	if bundle.RecipientUhid == s.sender.LocalUhid() {
		bundle.Status = models.DtnStatusDelivered
		_ = s.store.Save(ctx, bundle)
		if rep := s.reputation; rep != nil {
			rep.RecordDeliverySuccess(packet.SourceUhid, 0)
		}
		return s.sendDeliveryReceipt(ctx, bundle)
	}

	count, _ := s.store.GetActiveCount(ctx)
	if int32(count) >= constants.DtnMaxBundlesPerNode {
		return s.sendCustodyAck(ctx, bundle.ID, packet.SourceUhid, false)
	}

	bundle.Status = models.DtnStatusInCustody
	bundle.HopCount++
	_ = s.store.Save(ctx, bundle)
	_ = s.store.SaveCustody(ctx, &models.CustodyRecord{
		ID:            uuid.NewString(),
		BundleID:      bundle.ID,
		FromUhid:      packet.SourceUhid,
		ToUhid:        s.sender.LocalUhid(),
		Accepted:      true,
		TransferredAt: time.Now(),
	})
	_ = s.incentives.RecordRelay(ctx, s.sender.LocalUhid(), packet)
	return s.sendCustodyAck(ctx, bundle.ID, packet.SourceUhid, true)
}

func (s *Service) handleCustodyAck(ctx context.Context, packet *protocol.MeshPacket) error {
	var ack custodyAckWire
	if err := json.Unmarshal(packet.Payload, &ack); err != nil {
		return fmt.Errorf("dtn: failed to deserialize custody ack: %w", err)
	}
	if ack.BundleID == "" {
		return nil
	}
	if !ack.Accepted {
		if rep := s.reputation; rep != nil {
			rep.RecordCustodyRefusal(packet.SourceUhid)
		}
		return nil
	}
	bundle, err := s.store.Get(ctx, ack.BundleID)
	if err != nil || bundle == nil {
		return err
	}
	bundle.CopyCount++
	return s.store.Save(ctx, bundle)
}

func (s *Service) handleDeliveryReceipt(ctx context.Context, packet *protocol.MeshPacket) error {
	var receipt deliveryReceiptWire
	if err := json.Unmarshal(packet.Payload, &receipt); err != nil {
		return fmt.Errorf("dtn: failed to deserialize delivery receipt: %w", err)
	}
	bundle, err := s.store.Get(ctx, receipt.BundleID)
	if err == nil && bundle != nil {
		bundle.Status = models.DtnStatusDelivered
		_ = s.store.Save(ctx, bundle)
	}
	if cb := s.OnBundleDelivered; cb != nil {
		cb(&models.DtnDeliveryReceipt{
			BundleID:              receipt.BundleID,
			RecipientUhid:         receipt.RecipientUhid,
			TotalHops:             receipt.TotalHops,
			TotalCustodyTransfers: receipt.TotalCustodyTransfers,
			DeliveredAt:           time.UnixMilli(receipt.DeliveredAtMs),
		})
	}
	return nil
}

func (s *Service) sendCustodyAck(ctx context.Context, bundleID, toUhid string, accepted bool) error {
	if toUhid == "" {
		return nil
	}
	body, err := json.Marshal(custodyAckWire{BundleID: bundleID, Accepted: accepted})
	if err != nil {
		return err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnCustodyAck
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = toUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, _ = s.sender.Send(ctx, pkt, toUhid)
	return nil
}

func (s *Service) sendDeliveryReceipt(ctx context.Context, bundle *models.DtnBundle) error {
	if bundle.SenderUhid == "" || bundle.SenderUhid == s.sender.LocalUhid() {
		return nil
	}
	custody, _ := s.store.GetCustodyRecords(ctx, bundle.ID)
	body, err := json.Marshal(deliveryReceiptWire{
		BundleID:              bundle.ID,
		RecipientUhid:         bundle.RecipientUhid,
		TotalHops:             bundle.HopCount,
		TotalCustodyTransfers: int32(len(custody)),
		DeliveredAtMs:         time.Now().UnixMilli(),
	})
	if err != nil {
		return err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnDeliveryReceipt
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = bundle.SenderUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, _ = s.sender.Send(ctx, pkt, bundle.SenderUhid)
	return nil
}

// ---- snake_case JSON wire shapes (cross-language stable) ------------------

type bundleWire struct {
	ID                   string             `json:"id"`
	SenderUhid           string             `json:"sender_uhid"`
	RecipientUhid        string             `json:"recipient_uhid"`
	EncryptedPayload     []byte             `json:"encrypted_payload"`
	Priority             models.DtnPriority `json:"priority"`
	Status               models.DtnStatus   `json:"status"`
	CopyCount            int32              `json:"copy_count"`
	MaxCopies            int32              `json:"max_copies"`
	SenderGeohash        string             `json:"sender_geohash"`
	RecipientLastGeohash string             `json:"recipient_last_geohash"`
	HopCount             int32              `json:"hop_count"`
	CreatedAtMs          int64              `json:"created_at_ms"`
	ExpiresAtMs          int64              `json:"expires_at_ms"`
}

func snakeCaseBundle(b *models.DtnBundle) bundleWire {
	return bundleWire{
		ID:                   b.ID,
		SenderUhid:           b.SenderUhid,
		RecipientUhid:        b.RecipientUhid,
		EncryptedPayload:     b.EncryptedPayload,
		Priority:             b.Priority,
		Status:               b.Status,
		CopyCount:            b.CopyCount,
		MaxCopies:            b.MaxCopies,
		SenderGeohash:        b.SenderGeohash,
		RecipientLastGeohash: b.RecipientLastGeohash,
		HopCount:             b.HopCount,
		CreatedAtMs:          b.CreatedAt.UnixMilli(),
		ExpiresAtMs:          b.ExpiresAt.UnixMilli(),
	}
}

func (w bundleWire) toBundle() *models.DtnBundle {
	return &models.DtnBundle{
		ID:                   w.ID,
		SenderUhid:           w.SenderUhid,
		RecipientUhid:        w.RecipientUhid,
		EncryptedPayload:     w.EncryptedPayload,
		Priority:             w.Priority,
		Status:               w.Status,
		CopyCount:            w.CopyCount,
		MaxCopies:            w.MaxCopies,
		SenderGeohash:        w.SenderGeohash,
		RecipientLastGeohash: w.RecipientLastGeohash,
		HopCount:             w.HopCount,
		CreatedAt:            time.UnixMilli(w.CreatedAtMs),
		ExpiresAt:            time.UnixMilli(w.ExpiresAtMs),
	}
}

type custodyAckWire struct {
	BundleID string `json:"bundle_id"`
	Accepted bool   `json:"accepted"`
}

type deliveryReceiptWire struct {
	BundleID              string `json:"bundle_id"`
	RecipientUhid         string `json:"recipient_uhid"`
	TotalHops             int32  `json:"total_hops"`
	TotalCustodyTransfers int32  `json:"total_custody_transfers"`
	DeliveredAtMs         int64  `json:"delivered_at_ms"`
}

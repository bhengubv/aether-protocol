// SPDX-License-Identifier: MIT

// Package extensibility defines the small set of optional seams hosts can wire
// up to participate in incentive accounting, cloud-relay fallbacks, and feature
// gating. Default no-op implementations let the protocol layer call through
// these uniformly without callers needing to check for nil.
package extensibility

import (
	"context"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// IncentiveProvider records relays for reward calculation and decides whether
// a packet jumps the priority queue. Default: no-op accounting; never prioritises.
type IncentiveProvider interface {
	// RecordRelay records that we just relayed a packet on behalf of someone.
	RecordRelay(ctx context.Context, localUhid string, packet *protocol.MeshPacket) error
	// ShouldPrioritize returns true if a packet should be sent ahead of the queue.
	ShouldPrioritize(ctx context.Context, packet *protocol.MeshPacket) bool
	// RecordCreatorTip is called when the local user tips a content author.
	// Distinct from RecordRelay (relay credit — paid to nodes that forward bytes);
	// this records direct creator -> consumer settlement (paid to the user who
	// AUTHORED the content). Host implementations (e.g. SDPKT, BhenguPay) wire
	// their settlement logic here. Default no-op does nothing.
	// Added in v1.2.0 — closes Issue #61 surfaced by Wave 16.
	RecordCreatorTip(ctx context.Context, creatorUhid string, amount float64, contentHash string) error
}

// BackendClient is the optional cloud-relay seam. Default: returns false everywhere.
type BackendClient interface {
	// RelayMessage forwards an opaque encrypted message to a backend for
	// delivery when no peer-to-peer route is available.
	RelayMessage(ctx context.Context, senderUhid, recipientUhid string, encryptedContent []byte, priority byte) (bool, error)
	// SyncDtnBundle hands a DTN bundle to a backend for store-and-forward.
	SyncDtnBundle(ctx context.Context, bundleJSON []byte) (bool, error)
	// SyncSos mirrors an SOS alert via cloud.
	SyncSos(ctx context.Context, alertJSON []byte) (bool, error)
}

// FeatureFlagProvider gates protocol features behind remote configuration.
// Default: every feature enabled.
type FeatureFlagProvider interface {
	IsEnabled(ctx context.Context, featureName string) bool
}

// NoopIncentiveProvider is the default no-op implementation.
type NoopIncentiveProvider struct{}

func (NoopIncentiveProvider) RecordRelay(ctx context.Context, localUhid string, packet *protocol.MeshPacket) error {
	return nil
}
func (NoopIncentiveProvider) ShouldPrioritize(ctx context.Context, packet *protocol.MeshPacket) bool {
	return false
}
func (NoopIncentiveProvider) RecordCreatorTip(ctx context.Context, creatorUhid string, amount float64, contentHash string) error {
	return nil
}

// NoopBackendClient is the default no-op implementation — every call returns false (offline-only mesh).
type NoopBackendClient struct{}

func (NoopBackendClient) RelayMessage(ctx context.Context, senderUhid, recipientUhid string, encryptedContent []byte, priority byte) (bool, error) {
	return false, nil
}
func (NoopBackendClient) SyncDtnBundle(ctx context.Context, bundleJSON []byte) (bool, error) {
	return false, nil
}
func (NoopBackendClient) SyncSos(ctx context.Context, alertJSON []byte) (bool, error) {
	return false, nil
}

// NoopFeatureFlagProvider returns true for every flag.
type NoopFeatureFlagProvider struct{}

func (NoopFeatureFlagProvider) IsEnabled(ctx context.Context, featureName string) bool { return true }

// SPDX-License-Identifier: MIT

package models

import (
	"time"

	"github.com/bhengubv/aether-protocol/go/identity"
)

// NodeCapabilities is a bitfield representing node capabilities.
type NodeCapabilities uint16

const (
	CapabilityBLE        NodeCapabilities = 1 << iota // Bluetooth Low Energy transport available
	CapabilityWifiDirect                              // Wi-Fi Direct transport available
	CapabilityGateway                                 // Internet gateway
	CapabilityRelay                                   // Willing to relay packets
	CapabilitySos                                     // SOS broadcast capable
	CapabilityStreaming                               // Live streaming relay capable
	CapabilityVoice                                   // Voice call relay capable
	CapabilityDtnCarrier                              // DTN store-and-forward carrier
	CapabilityNearLink                                // NearLink transport available
	CapabilityVideo                                   // Video call capable
)

// AetherNode represents a node in the mesh network.
type AetherNode struct {
	// Universal Hardware Identifier
	UHID string

	// Ed25519 public key (32 bytes)
	IdentityKey []byte

	// Human-readable identity address derived from IdentityKey.
	// Populated by calling AetherNode.DeriveTag() after IdentityKey is set.
	Tag identity.AetherTag

	// Node capabilities bitfield
	Capabilities NodeCapabilities

	// True if this is the local node
	IsLocal bool

	// Timestamp when this node was last seen
	LastSeen time.Time

	// Reliability score (0-100)
	ReliabilityScore int32
}

// DeriveTag computes the AetherTag from the node's IdentityKey and stores it
// in the Tag field.  It returns an error if IdentityKey is not 32 bytes.
func (n *AetherNode) DeriveTag() error {
	tag, err := identity.FromPublicKey(n.IdentityKey)
	if err != nil {
		return err
	}
	n.Tag = tag
	return nil
}

// PeerInfo represents information about a connected peer.
type PeerInfo struct {
	// Peer's UHID
	UHID string

	// List of reachable addresses (IP:port, BLE UUID, etc.)
	Addresses []string

	// Node capabilities
	Capabilities NodeCapabilities

	// Geohash of this peer's last-known location (privacy-gated; empty if not
	// shared). Used by the DTN replication strategy to rank carriers by their
	// proximity to the bundle recipient's last-known geohash.
	Geohash string

	// Last seen timestamp
	LastSeen time.Time

	// Hop count to this peer
	HopCount int32

	// Reliability score
	ReliabilityScore int32
}

// RouteEntry represents a route to a destination node.
type RouteEntry struct {
	// Destination UHID
	DestinationUhid string

	// Next hop UHID
	NextHop string

	// Hop count
	HopCount int32

	// Route expiration timestamp
	ExpiresAt time.Time

	// Quality score (0-100)
	QualityScore int32

	// Source UHID that initiated the route
	SourceUhid string
}

// IsStalc checks if this route has expired.
func (re *RouteEntry) IsStale() bool {
	return time.Now().After(re.ExpiresAt)
}

// DtnBundle represents a delay-tolerant bundle for store-and-forward delivery.
type DtnBundle struct {
	// Unique bundle identifier
	ID string

	// Originator's UHID
	SenderUhid string

	// Intended recipient's UHID
	RecipientUhid string

	// End-to-end encrypted content
	EncryptedPayload []byte

	// Priority level
	Priority DtnPriority

	// Current delivery status
	Status DtnStatus

	// Number of copies in the network
	CopyCount int32

	// Maximum allowed copies
	MaxCopies int32

	// Sender's geohash at creation
	SenderGeohash string

	// Recipient's last known geohash
	RecipientLastGeohash string

	// Number of custody transfers completed
	HopCount int32

	// Bundle creation timestamp
	CreatedAt time.Time

	// Bundle expiration timestamp
	ExpiresAt time.Time
}

// DtnPriority represents DTN bundle priority.
type DtnPriority byte

const (
	DtnPriorityLow DtnPriority = iota
	DtnPriorityNormal
	DtnPriorityHigh
	DtnPrioritySos
)

// DtnStatus represents DTN bundle delivery status.
type DtnStatus byte

const (
	DtnStatusPending DtnStatus = iota
	DtnStatusInCustody
	DtnStatusDelivered
	DtnStatusExpired
	DtnStatusFailed
)

// PresenceBeacon represents a node's presence announcement.
type PresenceBeacon struct {
	// Node UHID
	UHID string

	// Node's current status (online, busy, away, etc.)
	Status PresenceStatus

	// Custom status message
	StatusMessage string

	// Timestamp of beacon
	Timestamp time.Time

	// Geohash (if privacy allows)
	Geohash string
}

// PresenceStatus represents a node's presence status.
type PresenceStatus byte

const (
	PresenceOnline PresenceStatus = iota
	PresenceBusy
	PresenceAway
	PresenceOffline
)

// SosAlert represents an SOS emergency broadcast.
type SosAlert struct {
	// Unique alert identifier
	ID string

	// Originator's UHID
	SenderUhid string

	// Caller-defined alert category — "sos", "panic", "medical", "fire", etc.
	BroadcastType string

	// Alert message
	Message string

	// Sender's location
	Latitude  float64
	Longitude float64

	// Geohash of sender
	Geohash string

	// Alert timestamp (origination)
	Timestamp time.Time

	// Local time the alert was received (or originated locally)
	ReceivedAt time.Time

	// AcknowledgedBy holds the distinct UHIDs of peers that have acknowledged
	// receiving this alert. Populated on the ORIGINATING node only, as SosAck
	// packets arrive back — it lets the sender see how many devices their
	// emergency reached. Access is synchronised by the SOS service. Mirrors the
	// C# SosAlert.AcknowledgedBy set.
	AcknowledgedBy map[string]struct{}
}

// SosAcknowledgement is raised on the originating node when a peer acknowledges
// receipt of one of its active SOS alerts. Mirrors the C# SosAcknowledgement.
type SosAcknowledgement struct {
	// Id of the SOS broadcast that was acknowledged.
	BroadcastID string

	// UHID of the peer that acknowledged receiving the SOS.
	ResponderUhid string

	// Total distinct peers that have acknowledged this SOS so far (this responder included).
	TotalAcknowledgements int
}

// CustodyRecord captures a DTN custody transfer between two nodes.
type CustodyRecord struct {
	ID            string
	BundleID      string
	FromUhid      string
	ToUhid        string
	Accepted      bool
	TransferredAt time.Time
}

// DtnDeliveryReceipt is sent back to the original sender once a bundle is delivered.
type DtnDeliveryReceipt struct {
	BundleID              string
	RecipientUhid         string
	TotalHops             int32
	TotalCustodyTransfers int32
	DeliveredAt           time.Time
}

// IsExpired returns true if the bundle has exceeded its TTL.
func (b *DtnBundle) IsExpired() bool {
	return time.Now().After(b.ExpiresAt)
}

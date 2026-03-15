// SPDX-License-Identifier: MIT

package models

import "time"

// NodeCapabilities is a bitfield representing node capabilities.
type NodeCapabilities byte

const (
	CapabilityBLE       NodeCapabilities = 1 << iota // Bluetooth Low Energy transport available
	CapabilityWifiDirect                             // Wi-Fi Direct transport available
	CapabilityGateway                                // Internet gateway
	CapabilityRelay                                  // Willing to relay packets
	CapabilitySos                                    // SOS broadcast capable
	CapabilityStreaming                              // Live streaming relay capable
	CapabilityVoice                                  // Voice call relay capable
	CapabilityDtnCarrier                             // DTN store-and-forward carrier
)

// AetherNode represents a node in the mesh network.
type AetherNode struct {
	// Universal Hardware Identifier
	UHID string

	// Ed25519 public key (32 bytes)
	IdentityKey []byte

	// Node capabilities bitfield
	Capabilities NodeCapabilities

	// True if this is the local node
	IsLocal bool

	// Timestamp when this node was last seen
	LastSeen time.Time

	// Reliability score (0-100)
	ReliabilityScore int32
}

// PeerInfo represents information about a connected peer.
type PeerInfo struct {
	// Peer's UHID
	UHID string

	// List of reachable addresses (IP:port, BLE UUID, etc.)
	Addresses []string

	// Node capabilities
	Capabilities NodeCapabilities

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

	// Alert message
	Message string

	// Sender's location
	Latitude  float64
	Longitude float64

	// Geohash of sender
	Geohash string

	// Alert timestamp
	Timestamp time.Time
}

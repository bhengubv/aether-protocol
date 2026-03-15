// SPDX-License-Identifier: MIT

package transport

import "context"

// TransportService defines the interface for physical layer transports.
type TransportService interface {
	// Name returns a human-readable identifier (e.g., "BLE", "Wi-Fi Direct").
	Name() string

	// IsAvailable returns whether the transport is currently usable.
	IsAvailable() bool

	// MaxBandwidthBps returns maximum throughput in bytes per second.
	MaxBandwidthBps() int64

	// MaxRangeMeters returns maximum communication range in meters.
	MaxRangeMeters() int32

	// PowerCostRelative returns relative power consumption (1 = low, 10 = high).
	PowerCostRelative() int32

	// MaxConcurrentPeers returns maximum simultaneous peer connections.
	MaxConcurrentPeers() int32

	// SendAsync sends a byte array to a specific peer.
	SendAsync(ctx context.Context, peerUhid string, data []byte) (bool, error)

	// SendStreamAsync sends a stream to a peer for large transfers.
	SendStreamAsync(ctx context.Context, peerUhid string, data []byte) (bool, error)

	// IsConnected checks if a connection is active to a peer.
	IsConnected(peerUhid string) bool
}

// TransportType represents the type of transport.
type TransportType int

const (
	BLE TransportType = iota
	WiFiDirect
	NearLink
)

// BLE Transport configuration
const (
	BleMaxPayloadBytes = 1024
)

// Wi-Fi Direct Transport configuration
const (
	WifiDirectTimeoutMs = 10000
	MaxWifiDirectPeers  = 8
)

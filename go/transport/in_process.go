// SPDX-License-Identifier: MIT

package transport

import (
	"context"
	"fmt"
	"sync"
)

// InProcessTransport is an in-memory transport for testing and inter-process communication.
// It uses a global sync.Map to route packets between nodes.
type InProcessTransport struct {
	name            string
	available       bool
	maxBandwidth    int64
	maxRange        int32
	powerCost       int32
	maxConcurrency  int32
	connectedPeers  sync.Map // map[string]bool
	messageHandlers sync.Map // map[string]chan []byte
	m               *PerTransportMetrics
}

// NewInProcessTransport creates a new in-memory transport.
func NewInProcessTransport() *InProcessTransport {
	return &InProcessTransport{
		name:           "InProcess",
		available:      true,
		maxBandwidth:   1000000, // 1 Mbps equivalent
		maxRange:       100,
		powerCost:      1,
		maxConcurrency: 100,
		m:              NewPerTransportMetrics(),
	}
}

// Name returns the transport name.
func (ipt *InProcessTransport) Name() string {
	return ipt.name
}

// IsAvailable returns whether the transport is available.
func (ipt *InProcessTransport) IsAvailable() bool {
	return ipt.available
}

// MaxBandwidthBps returns maximum bandwidth.
func (ipt *InProcessTransport) MaxBandwidthBps() int64 {
	return ipt.maxBandwidth
}

// MaxRangeMeters returns maximum range.
func (ipt *InProcessTransport) MaxRangeMeters() int32 {
	return ipt.maxRange
}

// PowerCostRelative returns relative power cost.
func (ipt *InProcessTransport) PowerCostRelative() int32 {
	return ipt.powerCost
}

// MaxConcurrentPeers returns max concurrent peers.
func (ipt *InProcessTransport) MaxConcurrentPeers() int32 {
	return ipt.maxConcurrency
}

// SendAsync sends data to a peer asynchronously.
func (ipt *InProcessTransport) SendAsync(ctx context.Context, peerUhid string, data []byte) (bool, error) {
	if !ipt.available {
		return false, fmt.Errorf("transport not available")
	}

	if len(peerUhid) == 0 {
		return false, fmt.Errorf("peer UHID cannot be empty")
	}

	if len(data) == 0 {
		return false, fmt.Errorf("data cannot be empty")
	}

	// Check if peer exists and has a message handler
	handlerInterface, ok := ipt.messageHandlers.Load(peerUhid)
	if !ok {
		return false, fmt.Errorf("peer %s not registered", peerUhid)
	}

	handler, ok := handlerInterface.(chan []byte)
	if !ok {
		return false, fmt.Errorf("invalid handler for peer %s", peerUhid)
	}

	// Send data to the peer's channel (non-blocking)
	select {
	case handler <- append([]byte{}, data...):
		ipt.m.RecordSample(0, true, int64(len(data)))
		return true, nil
	case <-ctx.Done():
		return false, ctx.Err()
	default:
		return false, fmt.Errorf("peer handler queue full for %s", peerUhid)
	}
}

// SendStreamAsync sends stream data to a peer (equivalent to SendAsync for in-process).
func (ipt *InProcessTransport) SendStreamAsync(ctx context.Context, peerUhid string, data []byte) (bool, error) {
	return ipt.SendAsync(ctx, peerUhid, data)
}

// IsConnected checks if a peer is registered in this transport.
func (ipt *InProcessTransport) IsConnected(peerUhid string) bool {
	_, exists := ipt.connectedPeers.Load(peerUhid)
	return exists
}

// RegisterPeer registers a peer in the transport and returns a receive channel.
func (ipt *InProcessTransport) RegisterPeer(peerUhid string, bufferSize int) (chan []byte, error) {
	if len(peerUhid) == 0 {
		return nil, fmt.Errorf("peer UHID cannot be empty")
	}

	handler := make(chan []byte, bufferSize)
	ipt.messageHandlers.Store(peerUhid, handler)
	ipt.connectedPeers.Store(peerUhid, true)

	return handler, nil
}

// UnregisterPeer unregisters a peer from the transport.
func (ipt *InProcessTransport) UnregisterPeer(peerUhid string) {
	handlerInterface, ok := ipt.messageHandlers.Load(peerUhid)
	if ok {
		if handler, ok := handlerInterface.(chan []byte); ok {
			close(handler)
		}
		ipt.messageHandlers.Delete(peerUhid)
	}
	ipt.connectedPeers.Delete(peerUhid)
}

// Metrics returns the per-transport EWMA metrics for this transport.
func (ipt *InProcessTransport) Metrics() *PerTransportMetrics {
	return ipt.m
}

// Shutdown cleanly shuts down the transport.
func (ipt *InProcessTransport) Shutdown() error {
	ipt.available = false
	// Unregister all peers
	ipt.messageHandlers.Range(func(key, value interface{}) bool {
		if peerUhid, ok := key.(string); ok {
			ipt.UnregisterPeer(peerUhid)
		}
		return true
	})
	return nil
}

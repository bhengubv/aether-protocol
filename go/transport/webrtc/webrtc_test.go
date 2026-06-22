// SPDX-License-Identifier: MIT

package webrtc

import (
	"context"
	"testing"
	"time"

	pion "github.com/pion/webrtc/v4"
)

// TestTwoPeersExchangeBytesNoServer stands up two real WebRtcTransport instances wired only through
// an in-process signalling bus — no central server, no STUN — and proves a direct data channel
// negotiates over host candidates and carries bytes.
func TestTwoPeersExchangeBytesNoServer(t *testing.T) {
	bus := NewInMemorySignalingBus()
	defer bus.Close()

	hostOnly := []pion.ICEServer{} // empty (not nil) => host-candidate-only ICE, no network dependency

	alice, err := NewWebRtcTransport("alice", bus.Endpoint("alice"), hostOnly)
	if err != nil {
		t.Fatalf("new alice: %v", err)
	}
	defer alice.Close()

	bob, err := NewWebRtcTransport("bob", bus.Endpoint("bob"), hostOnly)
	if err != nil {
		t.Fatalf("new bob: %v", err)
	}
	defer bob.Close()

	got := make(chan []byte, 1)
	bob.OnDataReceived(func(from string, data []byte) {
		if from == "alice" {
			got <- data
		}
	})

	payload := []byte("hello over a serverless webrtc datachannel")
	ok, err := alice.SendAsync(context.Background(), "bob", payload)
	if err != nil || !ok {
		t.Fatalf("send: ok=%v err=%v", ok, err)
	}

	select {
	case data := <-got:
		if string(data) != string(payload) {
			t.Fatalf("payload mismatch: got %q want %q", data, payload)
		}
	case <-time.After(30 * time.Second):
		t.Fatal("timed out waiting for bytes over the data channel")
	}

	if !alice.IsConnected("bob") {
		t.Error("alice should report connected to bob")
	}
	if !bob.IsConnected("alice") {
		t.Error("bob should report connected to alice")
	}
}

// TestTransportMetadata checks the ladder-facing metadata.
func TestTransportMetadata(t *testing.T) {
	bus := NewInMemorySignalingBus()
	defer bus.Close()

	tr, err := NewWebRtcTransport("x", bus.Endpoint("x"), []pion.ICEServer{})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	defer tr.Close()

	if tr.Name() != "WebRTC P2P" {
		t.Errorf("Name = %q", tr.Name())
	}
	if !tr.IsAvailable() {
		t.Error("should be available")
	}
	if tr.MaxRangeMeters() != 0 {
		t.Errorf("internet range should be 0 (unbounded), got %d", tr.MaxRangeMeters())
	}
}

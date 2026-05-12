// SPDX-License-Identifier: MIT

package security

import (
	"crypto/sha256"
	"encoding/binary"
	"fmt"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/reputation"
)

// PacketSigningService handles packet signing and nonce deduplication.
type PacketSigningService struct {
	mu             sync.RWMutex
	nonceCache     map[string]int64 // key: "source_uhid:nonce", value: timestamp
	maxPacketAge   int32            // seconds
	cleanupTicker  *time.Ticker
	done           chan struct{}
	reputation     *reputation.NodeReputationService
}

// NewPacketSigningService creates a new packet signing service.
func NewPacketSigningService(maxPacketAgeSec int32) *PacketSigningService {
	pss := &PacketSigningService{
		nonceCache:    make(map[string]int64),
		maxPacketAge:  maxPacketAgeSec,
		cleanupTicker: time.NewTicker(60 * time.Second),
		done:          make(chan struct{}),
	}

	// Start background cleanup goroutine
	go pss.cleanupLoop()

	return pss
}

// ComputeSignableData constructs the deterministic byte sequence for signing.
// Format: PacketNonce || TimestampMs (LE) || Type (LE) || SourceUhidLength (LE) ||
//         SourceUhid || DestinationUhidLength (LE) || DestinationUhid ||
//         SHA256(Payload) || Ttl (LE) || Priority (LE)
func (pss *PacketSigningService) ComputeSignableData(
	nonce []byte,
	timestampMs int64,
	packetType byte,
	sourceUhid string,
	destUhid string,
	payload []byte,
	ttl int32,
	priority byte,
) []byte {
	buf := make([]byte, 0, 256)

	// Nonce (8 bytes)
	buf = append(buf, nonce...)

	// TimestampMs (8 bytes, little-endian int64)
	ts := make([]byte, 8)
	binary.LittleEndian.PutUint64(ts, uint64(timestampMs))
	buf = append(buf, ts...)

	// Type (4 bytes, little-endian int32)
	typ := make([]byte, 4)
	binary.LittleEndian.PutUint32(typ, uint32(packetType))
	buf = append(buf, typ...)

	// SourceUhid (4-byte LE length + UTF-8 bytes)
	srcLen := make([]byte, 4)
	binary.LittleEndian.PutUint32(srcLen, uint32(len(sourceUhid)))
	buf = append(buf, srcLen...)
	buf = append(buf, []byte(sourceUhid)...)

	// DestinationUhid (4-byte LE length + UTF-8 bytes)
	dstLen := make([]byte, 4)
	binary.LittleEndian.PutUint32(dstLen, uint32(len(destUhid)))
	buf = append(buf, dstLen...)
	buf = append(buf, []byte(destUhid)...)

	// SHA-256(Payload) (32 bytes)
	h := sha256.Sum256(payload)
	buf = append(buf, h[:]...)

	// Ttl (4 bytes, little-endian int32)
	ttlBytes := make([]byte, 4)
	binary.LittleEndian.PutUint32(ttlBytes, uint32(ttl))
	buf = append(buf, ttlBytes...)

	// Priority (4 bytes, little-endian int32)
	priBytes := make([]byte, 4)
	binary.LittleEndian.PutUint32(priBytes, uint32(priority))
	buf = append(buf, priBytes...)

	return buf
}

// IsNonceSeen checks if a nonce has been seen from a source within the TTL.
// Returns true if duplicate, false if new.
func (pss *PacketSigningService) IsNonceSeen(sourceUhid string, nonce []byte) bool {
	key := fmt.Sprintf("%s:%x", sourceUhid, nonce)

	pss.mu.RLock()
	_, exists := pss.nonceCache[key]
	pss.mu.RUnlock()

	return exists
}

// RecordNonce records a nonce for a source.
func (pss *PacketSigningService) RecordNonce(sourceUhid string, nonce []byte) {
	key := fmt.Sprintf("%s:%x", sourceUhid, nonce)
	now := time.Now().Unix()

	pss.mu.Lock()
	pss.nonceCache[key] = now
	pss.mu.Unlock()
}

// SetReputation wires a NodeReputationService into the signing service so that
// replay and signature-failure events are reflected in the reputation ledger.
// Safe to call concurrently; passing nil removes the current hook.
func (pss *PacketSigningService) SetReputation(r *reputation.NodeReputationService) {
	pss.mu.Lock()
	pss.reputation = r
	pss.mu.Unlock()
}

// ValidateAndRecordNonce atomically checks whether the (sourceUhid, nonce) pair
// has been seen before and, if not, records it.
//
// Returns true when the nonce is fresh (the caller should accept the packet).
// Returns false when the nonce is a duplicate (the caller should drop the
// packet). On a duplicate, RecordReplayAttempt is fired on the wired
// reputation service if one has been set.
//
// This method replaces the pattern of calling IsNonceSeen + RecordNonce in
// separate steps; the existing methods are kept for backward compatibility.
func (pss *PacketSigningService) ValidateAndRecordNonce(sourceUhid string, nonce []byte) bool {
	key := fmt.Sprintf("%s:%x", sourceUhid, nonce)
	now := time.Now().Unix()

	pss.mu.Lock()
	_, seen := pss.nonceCache[key]
	if seen {
		rep := pss.reputation
		pss.mu.Unlock()
		if rep != nil {
			rep.RecordReplayAttempt(sourceUhid)
		}
		return false
	}
	pss.nonceCache[key] = now
	pss.mu.Unlock()

	return true
}

// NotifySignatureFailure fires the reputation hook for a failed Ed25519
// signature verification. Call this immediately after a verify step returns
// false so the reputation ledger can penalise the sending node.
func (pss *PacketSigningService) NotifySignatureFailure(sourceUhid string) {
	pss.mu.RLock()
	rep := pss.reputation
	pss.mu.RUnlock()
	if rep != nil {
		rep.RecordSignatureFailure(sourceUhid)
	}
}

// cleanupLoop periodically removes expired nonce entries.
func (pss *PacketSigningService) cleanupLoop() {
	for {
		select {
		case <-pss.cleanupTicker.C:
			pss.cleanup()
		case <-pss.done:
			return
		}
	}
}

// cleanup removes expired entries from the nonce cache.
func (pss *PacketSigningService) cleanup() {
	now := time.Now().Unix()
	maxAgeUnix := int64(pss.maxPacketAge)

	pss.mu.Lock()
	for key, timestamp := range pss.nonceCache {
		if now-timestamp > maxAgeUnix {
			delete(pss.nonceCache, key)
		}
	}
	pss.mu.Unlock()
}

// Close stops the background cleanup goroutine.
func (pss *PacketSigningService) Close() {
	close(pss.done)
	pss.cleanupTicker.Stop()
}

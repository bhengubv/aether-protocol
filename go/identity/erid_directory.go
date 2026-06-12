// SPDX-License-Identifier: MIT

package identity

import (
	"errors"
	"sync"
)

// EridDirectory resolves rotating ERID wire addresses to and from the stable peer
// identities behind them — the piece that lets an ESTABLISHED relationship follow a
// peer's rotating address while an outsider cannot.
//
// A node derives its OWN secret routingKey once (via DeriveRoutingKey) and shares it
// with a peer INSIDE the established Signal session — never on the wire. Each side
// stores the other's routingKey here, so either can compute the other's current ERID
// for addressing, and reverse-resolve an inbound ERID back to the peer it belongs to.
// An outsider holds no routingKey and can do neither: to a passive observer the ERID is
// an opaque value that rotates every epoch with no cross-window linkage.
//
// This is the in-memory directory only — additive, off-wire. Safe for concurrent use.
type EridDirectory struct {
	mu           sync.RWMutex
	myRoutingKey []byte
	epochSeconds int
	eridLength   int
	peerKeys     map[string][]byte // peerUhid -> that peer's secret routingKey
}

// NewEridDirectory creates a directory for a node holding myRoutingKey (copied
// defensively). epochSeconds and eridLength default to DefaultEpochSeconds /
// DefaultEridLength when passed as 0. Returns an error if myRoutingKey is empty.
func NewEridDirectory(myRoutingKey []byte, epochSeconds, eridLength int) (*EridDirectory, error) {
	if len(myRoutingKey) == 0 {
		return nil, errors.New("erid: myRoutingKey cannot be empty")
	}
	if epochSeconds == 0 {
		epochSeconds = DefaultEpochSeconds
	}
	if eridLength == 0 {
		eridLength = DefaultEridLength
	}
	if epochSeconds <= 0 {
		return nil, errors.New("erid: epochSeconds must be positive")
	}
	key := make([]byte, len(myRoutingKey))
	copy(key, myRoutingKey)
	return &EridDirectory{
		myRoutingKey: key,
		epochSeconds: epochSeconds,
		eridLength:   eridLength,
		peerKeys:     make(map[string][]byte),
	}, nil
}

// MyErid returns our own current ERID for the epoch containing unixSeconds — the
// address we present on the wire this window.
func (d *EridDirectory) MyErid(unixSeconds int64) (string, error) {
	d.mu.RLock()
	defer d.mu.RUnlock()
	return DeriveERID(d.myRoutingKey, unixSeconds, d.epochSeconds, d.eridLength)
}

// RememberPeer stores a peer's routingKey, learned inside an established session.
// Idempotent; a later call replaces an earlier key for the same peer (e.g. after a
// re-key). Returns an error if peerUhid or peerRoutingKey is empty.
func (d *EridDirectory) RememberPeer(peerUhid string, peerRoutingKey []byte) error {
	if peerUhid == "" {
		return errors.New("erid: peerUhid cannot be empty")
	}
	if len(peerRoutingKey) == 0 {
		return errors.New("erid: peerRoutingKey cannot be empty")
	}
	key := make([]byte, len(peerRoutingKey))
	copy(key, peerRoutingKey)
	d.mu.Lock()
	defer d.mu.Unlock()
	d.peerKeys[peerUhid] = key
	return nil
}

// ForgetPeer removes a peer (session torn down, or peer excommunicated). Returns false
// if the peer was unknown.
func (d *EridDirectory) ForgetPeer(peerUhid string) bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	if _, ok := d.peerKeys[peerUhid]; !ok {
		return false
	}
	delete(d.peerKeys, peerUhid)
	return true
}

// EridForPeer returns the current ERID a known peer presents this epoch. The bool is
// false (and the string empty) if we hold no key for that peer.
func (d *EridDirectory) EridForPeer(peerUhid string, unixSeconds int64) (string, bool, error) {
	d.mu.RLock()
	defer d.mu.RUnlock()
	key, ok := d.peerKeys[peerUhid]
	if !ok {
		return "", false, nil
	}
	erid, err := DeriveERID(key, unixSeconds, d.epochSeconds, d.eridLength)
	if err != nil {
		return "", false, err
	}
	return erid, true, nil
}

// ResolvePeer reverse-resolves an inbound wire ERID to the stable peer UHID behind it
// for the given epoch. The bool is false (and the string empty) if no known peer
// currently presents it. O(n) over known peers — a node's actual relationship count.
func (d *EridDirectory) ResolvePeer(erid string, unixSeconds int64) (string, bool, error) {
	if erid == "" {
		return "", false, nil
	}
	d.mu.RLock()
	defer d.mu.RUnlock()
	for uhid, key := range d.peerKeys {
		candidate, err := DeriveERID(key, unixSeconds, d.epochSeconds, d.eridLength)
		if err != nil {
			return "", false, err
		}
		if candidate == erid {
			return uhid, true, nil
		}
	}
	return "", false, nil
}

// KnownPeerCount is the number of peers whose routingKey we currently hold.
func (d *EridDirectory) KnownPeerCount() int {
	d.mu.RLock()
	defer d.mu.RUnlock()
	return len(d.peerKeys)
}

// SPDX-License-Identifier: MIT

package handshake

import "time"

// PeerCapabilities is the negotiated protocol-version + capability set for
// a remote peer, locked in once the Hello/HelloAck exchange completes
// (or after the backward-compat fallback for peers that never replied).
//
// NegotiatedVersion is the highest protocol version both sides advertised
// support for. Capabilities is the intersection of both sides' advertised
// capability tags — services should gate optional features (Double-Ratchet,
// DTN custody, voice, etc.) on capability presence rather than on raw
// protocol-version.
type PeerCapabilities struct {
	// PeerUhid is the UHID of the peer this record describes.
	PeerUhid string

	// NegotiatedVersion is the highest mutually-supported protocol
	// version. Defaults to 1 for peers that never replied with a HelloAck
	// (backward-compat).
	NegotiatedVersion byte

	// Capabilities is the intersection of capability tags both sides
	// claim to support. Empty for peers that never replied.
	Capabilities map[string]struct{}

	// ImplementationVersion is the free-form implementation banner the
	// peer announced (e.g. "aether-csharp/1.0.0"). Empty for peers that
	// never replied.
	ImplementationVersion string

	// NegotiatedAt is the UTC timestamp when negotiation completed.
	NegotiatedAt time.Time
}

// HasCapability reports whether the negotiated capability set contains the
// given tag.
func (p *PeerCapabilities) HasCapability(tag string) bool {
	if p == nil || p.Capabilities == nil {
		return false
	}
	_, ok := p.Capabilities[tag]
	return ok
}

// IncompatiblePeerEvent is fired when a peer's announced version range does
// not overlap with ours — we cannot speak to them. Subscribers should drop
// the peer from their connected-peer set.
type IncompatiblePeerEvent struct {
	// PeerUhid is the UHID of the incompatible peer.
	PeerUhid string

	// TheirMinVersion is the lowest version the peer claimed to support.
	TheirMinVersion byte

	// TheirMaxVersion is the highest version the peer claimed to support.
	TheirMaxVersion byte

	// OurMinVersion is the lowest version we accept.
	OurMinVersion byte

	// OurMaxVersion is the highest version we speak.
	OurMaxVersion byte

	// Reason is a human-readable explanation for the mismatch.
	Reason string
}

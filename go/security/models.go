// SPDX-License-Identifier: MIT

package security

import "time"

// PreKeyBundle represents a pre-key bundle published by a node so other
// nodes can initiate Signal sessions toward it asynchronously.
//
// Two identity keys per node — Ed25519 for signing and X25519 for ECDH.
// Keeping them separate (rather than using XEdDSA) is the simpler choice
// across the 8-language implementation family.
type PreKeyBundle struct {
	// Universal Hardware Identifier of the bundle owner.
	Uhid string

	// Long-term Ed25519 identity public key (32 bytes). Used to verify
	// SignedPreKeySignature.
	IdentityKey []byte

	// Long-term X25519 identity public key (32 bytes). Used as the second
	// DH counterparty in X3DH.
	IdentityKeyX25519 []byte

	// One-time pre-key id (consumed exactly once).
	PreKeyID int32

	// One-time pre-key X25519 public key (32 bytes raw, RFC 7748).
	PreKey []byte

	// Signed pre-key id (rotated periodically).
	SignedPreKeyID int32

	// Signed pre-key X25519 public key (32 bytes raw, RFC 7748).
	SignedPreKey []byte

	// Ed25519 signature over SignedPreKey bytes (64 bytes).
	SignedPreKeySignature []byte
}

// EncryptedPayload is the wire-level form of an encrypted message.
//
// Two layered ratchets contribute fields:
//
//  1. X3DH session-establishment (Signal §3) — populated only on the very
//     first message a new initiator sends to a peer (MessageType=PreKey):
//     InitiatorIdentityKeyX25519, UsedSignedPreKeyID, UsedOneTimePreKeyID.
//     The responder uses these to run X3DH on its side and derive the same
//     root key.
//
//  2. Double Ratchet (Signal §5) — SenderEphemeralKeyX25519 and
//     PreviousChainCount populated on EVERY message.
//     SenderEphemeralKeyX25519 is the sender's current DH-ratchet public key;
//     when it changes between messages, the receiver runs a DH-ratchet step
//     that re-keys the chain and gives per-roundtrip forward secrecy and
//     post-compromise security. On the very first PreKey message, this
//     equals the X3DH ephemeral public key (Signal-canonical integration:
//     initiator's X3DH ephemeral becomes its first DH-ratchet public).
type EncryptedPayload struct {
	// AES-256-GCM ciphertext concatenated with the 16-byte authentication tag.
	Ciphertext []byte

	// AES-GCM nonce (12 bytes, freshly random per message).
	Nonce []byte

	// 0 = normal session message, 1 = PreKey (initial) message.
	MessageType int32

	// Sender's UHID — set to the local node's UHID when encrypting.
	SenderUhid string

	// Message counter within the current sending chain (Signal §5: Ns).
	Counter int32

	// PreKey messages: initiator's long-term X25519 identity public key
	// (32 bytes). Nil on normal messages.
	InitiatorIdentityKeyX25519 []byte

	// DEPRECATED: use SenderEphemeralKeyX25519 instead. Kept for backward
	// compatibility with consumers of the pre-Double-Ratchet wire envelope.
	// On PreKey messages this equals SenderEphemeralKeyX25519; on normal
	// messages it is nil. New consumers should ignore this field.
	InitiatorEphemeralKeyX25519 []byte

	// PreKey messages: the SignedPreKeyID from the recipient's bundle that
	// the initiator consumed. 0 on normal messages.
	UsedSignedPreKeyID int32

	// PreKey messages: the one-time PreKeyID from the recipient's bundle
	// that the initiator consumed. 0 on normal messages.
	UsedOneTimePreKeyID int32

	// SenderEphemeralKeyX25519 is the sender's current DH-ratchet X25519
	// public key (32 bytes). Populated on EVERY message. Drives the
	// DH-ratchet step on the receiver side: when this value changes between
	// successive messages from the same peer, the receiver re-keys the chain
	// via KDF_RK(rootKey, DH(myDHs, newDHr)).
	SenderEphemeralKeyX25519 []byte

	// PreviousChainCount is the number of messages the sender sent in its
	// previous sending chain (Signal §5: PN). Used by the receiver to
	// compute skipped message keys when crossing a DH-ratchet boundary.
	PreviousChainCount int32
}

// SignalSession tracks the state of a Signal Protocol session with a single peer.
//
// Double-Ratchet state per Signal §5:
//   - RK — root key. Re-keyed on every DH-ratchet step.
//   - DHs (priv/pub) — my current ratchet keypair (MyEphemeralPriv / MyEphemeralPub).
//   - DHr — peer's last-known ratchet public key (RemoteEphemeralPub). Nil
//     until first DH-ratchet step on the responder side.
//   - CKs — my current sending chain key (SendChainKey). Nil until I've
//     sent (or initialized) on this chain.
//   - CKr — my current receiving chain key (RecvChainKey). Nil until I've
//     received on this chain.
//   - Ns / Nr — send / receive counters (reset to 0 on each DH-ratchet step).
//   - PN — number of messages I sent in my previous sending chain
//     (PreviousChainCount), so the receiver can compute skipped keys across
//     a DH-ratchet boundary.
//   - MKSKIPPED — skipped message keys keyed by (DHr_pub, counter)
//     (SkippedMessageKeys).
type SignalSession struct {
	RootKey []byte

	// SendChainKey is my current sending chain key (Signal §5: CKs). Nil
	// until the first send (or until DH-ratchet rekeys it).
	SendChainKey []byte
	// RecvChainKey is my current receiving chain key (Signal §5: CKr). Nil
	// until the first receive that triggers a DH-ratchet step.
	RecvChainKey []byte

	SendCounter int32
	RecvCounter int32
	// PreviousChainCount is the number of messages sent in the previous
	// sending chain (Signal §5: PN).
	PreviousChainCount int32

	// MyEphemeralPriv is my current DH-ratchet private key (X25519, 32 bytes).
	MyEphemeralPriv []byte
	// MyEphemeralPub is my current DH-ratchet public key (X25519, 32 bytes).
	MyEphemeralPub []byte
	// RemoteEphemeralPub is the peer's last-seen DH-ratchet public key. Nil
	// until first DH-ratchet step (responder side starts nil so the first
	// receive triggers the ratchet).
	RemoteEphemeralPub []byte

	// SkippedMessageKeys are skipped message keys keyed by
	// "Hex(remoteDhrPub):counter". The remote-DHr-pub binding is essential —
	// out-of-order messages from a previous chain (different DHr) can still
	// arrive after a DH-ratchet step, and they need their own per-chain key
	// set rather than being conflated with the new chain's counters.
	SkippedMessageKeys map[string][]byte

	// PendingPreKeyMessage is true iff this session was established in the
	// initiator role and the first outbound message has not yet been sent.
	// While true, the next Encrypt emits a PreKey message (MessageType=1)
	// carrying the X3DH inputs.
	PendingPreKeyMessage       bool
	InitiatorIdentityKeyX25519 []byte
	UsedSignedPreKeyID         int32
	UsedOneTimePreKeyID        int32
}

// NewSignalSession creates a new signal session.
func NewSignalSession() *SignalSession {
	return &SignalSession{
		SkippedMessageKeys: make(map[string][]byte),
	}
}

// preKeyState holds the responder-side pre-key private halves so X3DH can
// be computed when an initiator's PreKey message arrives.
//
// One-time pre-keys are managed as a pool of opkPoolSize (default 100)
// entries:
//
//   - oneTimePreKeys is the authoritative map of OPKs the responder still
//     holds (un-issued + issued-but-not-yet-consumed). An OPK is removed
//     and zeroed on consumption, so a missing id => already consumed (or
//     never generated). Required for delayed PreKey messages.
//
//   - availableOpkIds is a FIFO queue of OPK ids that exist in
//     oneTimePreKeys and have NOT yet been issued in any bundle. Bundle
//     generation pops from the front; top-up runs each time a bundle is
//     generated so the queue never empties under steady load.
//
// signedPreKeyHistory holds the active SPK plus retained prior entries
// (oldest first, newest last). The newest entry is the active SPK that
// gets handed out in bundles. Retained prior entries let messages signed
// under a recently-rotated SPK still complete X3DH during the rotation
// window — Signal §3.3 recommends weekly rotation.
//
// signedPreKeyID / signedPreKeyPriv / signedPreKeyPub / signedPreKeySignature
// are denormalised mirrors of the LAST entry in signedPreKeyHistory, kept
// for the existing fast-path code that references them directly without a
// list lookup.
type preKeyState struct {
	signedPreKeyID        int32
	signedPreKeyPriv      []byte
	signedPreKeyPub       []byte
	signedPreKeySignature []byte

	// signedPreKeyHistory is the SPK history: oldest first, newest last.
	// The newest entry is the active SPK; older entries are retained for
	// the rotation window so messages signed under a recently-rotated SPK
	// can still decrypt.
	signedPreKeyHistory []signedPreKeyEntry

	// oneTimePreKeys holds every OPK keypair we still own — both un-issued
	// (still queued in availableOpkIds) and issued-but-not-yet-consumed
	// (already handed out in a bundle but no PreKey message has arrived).
	// Removed and zeroed on consumption.
	oneTimePreKeys map[int32]oneTimePreKey

	// availableOpkIds is a FIFO queue of OPK ids that are present in
	// oneTimePreKeys and have NOT yet been issued in any bundle. Bundle
	// generation dequeues from the front (FIFO). Top-up enqueues newly
	// generated ids. Must stay in sync with oneTimePreKeys.
	availableOpkIds []int32
}

// signedPreKeyEntry is one entry in the SPK history. The private half is
// held so responder-side X3DH can still complete when a peer presents a
// slightly-stale SPK during the rotation window.
type signedPreKeyEntry struct {
	id          int32
	priv        []byte
	pub         []byte
	signature   []byte
	generatedAt time.Time
}

type oneTimePreKey struct {
	priv []byte
	pub  []byte
}

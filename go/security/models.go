// SPDX-License-Identifier: MIT

package security

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
// When MessageType is 1 (PreKey message — the first message from an initiator
// before a session is established on the responder side), the four
// Initiator* fields carry the data the responder needs to run X3DH on its
// side and derive the same root key. On normal session messages
// (MessageType=0) those fields are nil/0.
type EncryptedPayload struct {
	// AES-256-GCM ciphertext concatenated with the 16-byte authentication tag.
	Ciphertext []byte

	// AES-GCM nonce (12 bytes, freshly random per message).
	Nonce []byte

	// 0 = normal session message, 1 = PreKey (initial) message.
	MessageType int32

	// Sender's UHID — set to the local node's UHID when encrypting.
	SenderUhid string

	// Message counter within the current sending chain.
	Counter int32

	// PreKey messages: initiator's long-term X25519 identity public key
	// (32 bytes). Nil on normal messages.
	InitiatorIdentityKeyX25519 []byte

	// PreKey messages: initiator's ephemeral X25519 public key (32 bytes,
	// generated fresh per session). Nil on normal messages.
	InitiatorEphemeralKeyX25519 []byte

	// PreKey messages: the SignedPreKeyID from the recipient's bundle that
	// the initiator consumed. 0 on normal messages.
	UsedSignedPreKeyID int32

	// PreKey messages: the one-time PreKeyID from the recipient's bundle
	// that the initiator consumed. 0 on normal messages.
	UsedOneTimePreKeyID int32
}

// SignalSession tracks the state of a Signal Protocol session with a single peer.
//
// On the initiator side (we processed the peer's pre-key bundle), the
// pending PreKey-message metadata is retained until the first message is
// sent — that first message carries our X25519 identity key, our fresh
// ephemeral public key, and the bundle ids consumed, so the responder can
// run X3DH on its side to derive the same root key.
type SignalSession struct {
	RootKey      []byte
	SendChainKey []byte
	RecvChainKey []byte
	SendCounter  int32
	RecvCounter  int32

	// Skipped message keys indexed by counter for out-of-order decryption.
	SkippedMessageKeys map[int32][]byte

	// True iff this session was established in the initiator role and the
	// first outbound message has not yet been sent.
	PendingPreKeyMessage         bool
	InitiatorIdentityKeyX25519   []byte
	InitiatorEphemeralKeyX25519  []byte
	UsedSignedPreKeyID           int32
	UsedOneTimePreKeyID          int32
}

// NewSignalSession creates a new signal session.
func NewSignalSession() *SignalSession {
	return &SignalSession{
		SkippedMessageKeys: make(map[int32][]byte),
	}
}

// preKeyState holds the responder-side pre-key private halves so X3DH can
// be computed when an initiator's PreKey message arrives.
type preKeyState struct {
	signedPreKeyID        int32
	signedPreKeyPriv      []byte
	signedPreKeyPub       []byte
	signedPreKeySignature []byte

	oneTimePreKeys map[int32]oneTimePreKey
}

type oneTimePreKey struct {
	priv []byte
	pub  []byte
}

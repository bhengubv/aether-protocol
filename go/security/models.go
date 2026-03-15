// SPDX-License-Identifier: MIT

package security

// PreKeyBundle represents a pre-key bundle for asynchronous session establishment.
type PreKeyBundle struct {
	// Node's Universal Hardware Identifier
	Uhid string

	// Ed25519 public key (32 bytes)
	IdentityKey []byte

	// Cryptographically random ID for one-time pre-key
	PreKeyID int32

	// ECDH P-256 public key (65 bytes uncompressed)
	PreKey []byte

	// Cryptographically random ID for signed pre-key
	SignedPreKeyID int32

	// ECDH P-256 public key (65 bytes uncompressed)
	SignedPreKey []byte

	// Ed25519 signature over SignedPreKey bytes
	SignedPreKeySignature []byte
}

// EncryptedPayload represents the encrypted payload format.
type EncryptedPayload struct {
	// AES-256-GCM ciphertext + 16-byte tag
	Ciphertext []byte

	// AES-GCM nonce (12 bytes)
	Nonce []byte

	// Message type: 1 = PreKey message, 2 = Regular
	MessageType int32

	// Sender's UHID
	SenderUhid string

	// Message sequence number within session
	Counter int32
}

// SignalSession tracks the state of a Signal Protocol session with a single peer.
type SignalSession struct {
	// Root key (32 bytes)
	RootKey []byte

	// Send chain key (32 bytes)
	SendChainKey []byte

	// Receive chain key (32 bytes)
	RecvChainKey []byte

	// Send counter
	SendCounter int32

	// Receive counter
	RecvCounter int32

	// Remote peer's public key
	RemotePublicKey []byte

	// Skipped message keys indexed by counter for out-of-order decryption
	SkippedMessageKeys map[int32][]byte
}

// NewSignalSession creates a new signal session.
func NewSignalSession() *SignalSession {
	return &SignalSession{
		SkippedMessageKeys: make(map[int32][]byte),
	}
}

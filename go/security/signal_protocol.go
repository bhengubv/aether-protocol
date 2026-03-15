// SPDX-License-Identifier: MIT

package security

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/ecdh"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"fmt"
	"sync"

	"golang.org/x/crypto/hkdf"
)

const (
	// AES-256-GCM parameters
	aesKeySize   = 32
	aesNonceSize = 12
	aesTagSize   = 16

	// Maximum number of skipped message keys per session
	MaxSkippedKeys = 1000
)

var (
	hkdfRootInfo       = []byte("aether-root-v1")
	hkdfChainSendInfo  = []byte("aether-chain-send-v1")
	hkdfChainRecvInfo  = []byte("aether-chain-recv-v1")
)

// SignalProtocolService implements the Signal Protocol for end-to-end encryption.
type SignalProtocolService struct {
	mu                    sync.RWMutex
	sessions              map[string]*SignalSession
	identityPrivateKey    []byte
	identityPublicKey     []byte
	ed25519PrivateKey     []byte
	ed25519PublicKey      []byte
	ed25519Service        *Ed25519Service
}

// NewSignalProtocolService creates a new Signal Protocol service.
func NewSignalProtocolService() (*SignalProtocolService, error) {
	ed25519Svc := NewEd25519Service()

	// Generate Ed25519 identity key pair
	privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()
	if err != nil {
		return nil, fmt.Errorf("failed to generate Ed25519 key pair: %w", err)
	}

	// Generate ECDH P-256 identity key pair
	ecdhPrivateKey, err := ecdh.P256().GenerateKey(rand.Reader)
	if err != nil {
		return nil, fmt.Errorf("failed to generate ECDH P-256 key pair: %w", err)
	}

	identityPrivate := ecdhPrivateKey.Bytes()
	identityPublic := ecdhPrivateKey.PublicKey().Bytes()

	return &SignalProtocolService{
		sessions:           make(map[string]*SignalSession),
		identityPrivateKey: identityPrivate,
		identityPublicKey:  identityPublic,
		ed25519PrivateKey:  privateKey,
		ed25519PublicKey:   publicKey,
		ed25519Service:     ed25519Svc,
	}, nil
}

// HasSession checks if a session exists with a peer.
func (sps *SignalProtocolService) HasSession(peerUhid string) bool {
	sps.mu.RLock()
	defer sps.mu.RUnlock()
	_, exists := sps.sessions[peerUhid]
	return exists
}

// Encrypt encrypts plaintext for a peer using an established session.
func (sps *SignalProtocolService) Encrypt(peerUhid string, plaintext []byte) (*EncryptedPayload, error) {
	sps.mu.Lock()
	defer sps.mu.Unlock()

	session, exists := sps.sessions[peerUhid]
	if !exists {
		return nil, fmt.Errorf("no session established with peer %s", peerUhid)
	}

	if plaintext == nil {
		return nil, fmt.Errorf("plaintext cannot be nil")
	}

	// Ratchet the sending chain
	newChainKey, messageKey, err := sps.ratchetChainKey(session.SendChainKey)
	if err != nil {
		return nil, fmt.Errorf("failed to ratchet chain key: %w", err)
	}

	session.SendChainKey = newChainKey
	defer ZeroMemory(messageKey)

	// Generate nonce
	nonce := make([]byte, aesNonceSize)
	if _, err := rand.Read(nonce); err != nil {
		return nil, fmt.Errorf("failed to generate nonce: %w", err)
	}

	// Encrypt with AES-256-GCM
	block, err := aes.NewCipher(messageKey)
	if err != nil {
		return nil, fmt.Errorf("failed to create AES cipher: %w", err)
	}

	aesgcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("failed to create GCM cipher: %w", err)
	}

	ciphertext := aesgcm.Seal(nil, nonce, plaintext, nil)

	counter := session.SendCounter
	session.SendCounter++

	return &EncryptedPayload{
		Ciphertext: ciphertext,
		Nonce:      nonce,
		MessageType: 2,
		SenderUhid: peerUhid,
		Counter:    counter,
	}, nil
}

// Decrypt decrypts an encrypted payload from a peer using an established session.
func (sps *SignalProtocolService) Decrypt(peerUhid string, payload *EncryptedPayload) ([]byte, error) {
	sps.mu.Lock()
	defer sps.mu.Unlock()

	session, exists := sps.sessions[peerUhid]
	if !exists {
		return nil, fmt.Errorf("no session established with peer %s", peerUhid)
	}

	if payload == nil {
		return nil, fmt.Errorf("payload cannot be nil")
	}

	if len(payload.Ciphertext) < aesTagSize {
		return nil, fmt.Errorf("ciphertext too short")
	}

	// Check for skipped message key
	if skippedKey, ok := session.SkippedMessageKeys[payload.Counter]; ok {
		delete(session.SkippedMessageKeys, payload.Counter)
		defer ZeroMemory(skippedKey)

		plaintext, err := sps.decryptWithKey(payload.Ciphertext, payload.Nonce, skippedKey)
		if err != nil {
			return nil, fmt.Errorf("failed to decrypt with skipped key: %w", err)
		}
		return plaintext, nil
	}

	// Check for excessive counter gap
	gap := payload.Counter - session.RecvCounter
	if gap > MaxSkippedKeys {
		return nil, fmt.Errorf("message counter gap (%d) exceeds maximum (%d), session must be re-established", gap, MaxSkippedKeys)
	}

	// Skip ahead and cache intermediate keys
	for session.RecvCounter < payload.Counter {
		newChainKey, skipKey, err := sps.ratchetChainKey(session.RecvChainKey)
		if err != nil {
			return nil, fmt.Errorf("failed to ratchet skip key: %w", err)
		}
		session.RecvChainKey = newChainKey
		session.SkippedMessageKeys[session.RecvCounter] = skipKey
		session.RecvCounter++
	}

	// Derive message key for current counter
	newChainKey, messageKey, err := sps.ratchetChainKey(session.RecvChainKey)
	if err != nil {
		return nil, fmt.Errorf("failed to ratchet message key: %w", err)
	}

	session.RecvChainKey = newChainKey
	session.RecvCounter++
	defer ZeroMemory(messageKey)

	plaintext, err := sps.decryptWithKey(payload.Ciphertext, payload.Nonce, messageKey)
	if err != nil {
		return nil, fmt.Errorf("failed to decrypt: %w", err)
	}

	return plaintext, nil
}

// GeneratePreKeyBundle generates a pre-key bundle for asynchronous session establishment.
func (sps *SignalProtocolService) GeneratePreKeyBundle(localUhid string) (*PreKeyBundle, error) {
	// Generate one-time pre-key (ECDH P-256)
	preKeyEcdh, err := ecdh.P256().GenerateKey(rand.Reader)
	if err != nil {
		return nil, fmt.Errorf("failed to generate pre-key: %w", err)
	}
	preKeyPublic := preKeyEcdh.PublicKey().Bytes()
	preKeyID, err := randomInt32()
	if err != nil {
		return nil, fmt.Errorf("failed to generate pre-key ID: %w", err)
	}

	// Generate signed pre-key (ECDH P-256)
	signedPreKeyEcdh, err := ecdh.P256().GenerateKey(rand.Reader)
	if err != nil {
		return nil, fmt.Errorf("failed to generate signed pre-key: %w", err)
	}
	signedPreKeyPublic := signedPreKeyEcdh.PublicKey().Bytes()
	signedPreKeyID, err := randomInt32()
	if err != nil {
		return nil, fmt.Errorf("failed to generate signed pre-key ID: %w", err)
	}

	// Sign the signed pre-key with Ed25519
	signature, err := sps.ed25519Service.Sign(sps.ed25519PrivateKey, signedPreKeyPublic)
	if err != nil {
		return nil, fmt.Errorf("failed to sign pre-key: %w", err)
	}

	return &PreKeyBundle{
		Uhid:                  localUhid,
		IdentityKey:           append([]byte{}, sps.ed25519PublicKey...),
		PreKeyID:              preKeyID,
		PreKey:                preKeyPublic,
		SignedPreKeyID:        signedPreKeyID,
		SignedPreKey:          signedPreKeyPublic,
		SignedPreKeySignature: signature,
	}, nil
}

// ProcessPreKeyBundle processes a pre-key bundle and establishes a session.
func (sps *SignalProtocolService) ProcessPreKeyBundle(bundle *PreKeyBundle) error {
	if bundle == nil {
		return fmt.Errorf("bundle cannot be nil")
	}

	// Verify the signed pre-key signature
	if !sps.ed25519Service.Verify(bundle.IdentityKey, bundle.SignedPreKey, bundle.SignedPreKeySignature) {
		return fmt.Errorf("signed pre-key signature verification failed")
	}

	sps.mu.Lock()
	defer sps.mu.Unlock()

	// Perform X3DH key agreement
	sharedSecret, err := sps.performX3DH(bundle.SignedPreKey, bundle.PreKey)
	if err != nil {
		return fmt.Errorf("failed to perform X3DH: %w", err)
	}
	defer ZeroMemory(sharedSecret)

	// Derive root key and initial chain keys
	rootKey := sps.deriveKey(sharedSecret, hkdfRootInfo)
	defer ZeroMemory(rootKey)

	sendChainKey := sps.deriveKey(rootKey, hkdfChainSendInfo)
	recvChainKey := sps.deriveKey(rootKey, hkdfChainRecvInfo)

	session := &SignalSession{
		RootKey:            rootKey,
		SendChainKey:       sendChainKey,
		RecvChainKey:       recvChainKey,
		RemotePublicKey:    append([]byte{}, bundle.IdentityKey...),
		SkippedMessageKeys: make(map[int32][]byte),
	}

	sps.sessions[bundle.Uhid] = session

	return nil
}

// SignData signs data using the local Ed25519 private key.
func (sps *SignalProtocolService) SignData(data []byte) ([]byte, error) {
	if data == nil {
		return nil, fmt.Errorf("data cannot be nil")
	}
	return sps.ed25519Service.Sign(sps.ed25519PrivateKey, data)
}

// VerifySignature verifies a signature using a public key.
func (sps *SignalProtocolService) VerifySignature(publicKey []byte, data []byte, signature []byte) bool {
	return sps.ed25519Service.Verify(publicKey, data, signature)
}

// GetPublicKey returns the Ed25519 public key.
func (sps *SignalProtocolService) GetPublicKey() []byte {
	return append([]byte{}, sps.ed25519PublicKey...)
}

// performX3DH performs X3DH key agreement.
func (sps *SignalProtocolService) performX3DH(remoteSignedPreKey, remotePreKey []byte) ([]byte, error) {
	// Reconstruct local ECDH private key
	localEcdh, err := ecdh.P256().NewPrivateKey(sps.identityPrivateKey)
	if err != nil {
		return nil, fmt.Errorf("failed to reconstruct local ECDH key: %w", err)
	}

	// DH1: identity <-> signed pre-key
	remoteSignedPk, err := ecdh.P256().NewPublicKey(remoteSignedPreKey)
	if err != nil {
		return nil, fmt.Errorf("failed to parse remote signed pre-key: %w", err)
	}
	dh1, err := localEcdh.ECDH(remoteSignedPk)
	if err != nil {
		return nil, fmt.Errorf("failed to perform DH1: %w", err)
	}
	defer ZeroMemory(dh1)

	// DH2: identity <-> one-time pre-key
	remotePreKeyPk, err := ecdh.P256().NewPublicKey(remotePreKey)
	if err != nil {
		return nil, fmt.Errorf("failed to parse remote pre-key: %w", err)
	}
	dh2, err := localEcdh.ECDH(remotePreKeyPk)
	if err != nil {
		return nil, fmt.Errorf("failed to perform DH2: %w", err)
	}
	defer ZeroMemory(dh2)

	// Concatenate DH results
	combined := make([]byte, len(dh1)+len(dh2))
	copy(combined, dh1)
	copy(combined[len(dh1):], dh2)

	return combined, nil
}

// deriveKey derives a 32-byte key using HKDF-SHA256.
func (sps *SignalProtocolService) deriveKey(ikm []byte, info []byte) []byte {
	h := hkdf.New(sha256.New, ikm, nil, info)
	key := make([]byte, aesKeySize)
	if _, err := h.Read(key); err != nil {
		panic(err) // Should never happen with HKDF
	}
	return key
}

// ratchetChainKey advances a chain key by one step using HMAC-SHA256.
// Returns (newChainKey, messageKey).
func (sps *SignalProtocolService) ratchetChainKey(chainKey []byte) ([]byte, []byte, error) {
	// Message key: HMAC-SHA256(chain key, 0x01)
	h1 := hmac.New(sha256.New, chainKey)
	h1.Write([]byte{0x01})
	messageKey := h1.Sum(nil)

	// Next chain key: HMAC-SHA256(chain key, 0x02)
	h2 := hmac.New(sha256.New, chainKey)
	h2.Write([]byte{0x02})
	newChainKey := h2.Sum(nil)

	return newChainKey, messageKey, nil
}

// decryptWithKey decrypts ciphertext using AES-256-GCM.
func (sps *SignalProtocolService) decryptWithKey(ciphertext, nonce, key []byte) ([]byte, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, fmt.Errorf("failed to create AES cipher: %w", err)
	}

	aesgcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("failed to create GCM cipher: %w", err)
	}

	plaintext, err := aesgcm.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return nil, fmt.Errorf("failed to decrypt: %w", err)
	}

	return plaintext, nil
}

// randomInt32 generates a random int32 in the range [1, 2^31-1].
func randomInt32() (int32, error) {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return 0, err
	}
	// Ensure positive and non-zero
	result := int32((uint32(b[0])<<24 | uint32(b[1])<<16 | uint32(b[2])<<8 | uint32(b[3])) & 0x7FFFFFFF)
	if result == 0 {
		result = 1
	}
	return result, nil
}

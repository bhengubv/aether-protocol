// SPDX-License-Identifier: MIT

package security

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"fmt"
	"sync"

	"golang.org/x/crypto/hkdf"
)

const (
	aesKeySize   = 32
	aesNonceSize = 12
	aesTagSize   = 16

	// MaxSkippedKeys is the maximum number of skipped message keys to retain
	// per session. If a counter gap exceeds this, the session must be
	// re-established.
	MaxSkippedKeys = 1000

	// MessageType values for EncryptedPayload.
	MessageTypeNormal = 0
	MessageTypePreKey = 1
)

// HKDF info strings for X3DH session establishment. The SAME info strings
// are used on initiator and responder sides; the responder SWAPS send/recv
// assignment so the initiator's send chain matches the responder's recv
// chain (and vice versa).
//
// These MUST match the C# reference (`SignalProtocolService.cs`) exactly —
// any drift breaks cross-language interop.
var (
	hkdfRootInfo               = []byte("aether-x3dh-root-v1")
	hkdfChainInitiatorSendInfo = []byte("aether-chain-initiator-send-v1")
	hkdfChainInitiatorRecvInfo = []byte("aether-chain-initiator-recv-v1")
)

// SignalProtocolService implements X3DH + Double-Ratchet for end-to-end
// encryption.
//
// Key agreement: X3DH (Signal §3) over X25519 (RFC 7748). Four DHs:
//   - DH1 = DH(IK_A, SPK_B) — long-term mutual auth
//   - DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
//   - DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
//   - DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
//
// Root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
// Symmetric ratchet: HMAC-SHA256, single-byte domain separation
//   (0x01 -> message key, 0x02 -> next chain key) per Signal §5.1.
// Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
// Signing: Ed25519.
type SignalProtocolService struct {
	mu sync.Mutex

	sessions map[string]*SignalSession

	// Long-term identity keys — two distinct keypairs per node.
	// X25519 for ECDH (X3DH); Ed25519 for signing.
	identityX25519Priv []byte
	identityX25519Pub  []byte
	ed25519PrivateKey  []byte
	ed25519PublicKey   []byte
	ed25519Service     *Ed25519Service

	// Local UHID — captured when GeneratePreKeyBundle is called or via
	// SetLocalUhid. Used as the SenderUhid on outbound EncryptedPayloads.
	localUhid string

	// Pre-key state held for responder-side X3DH.
	preKeys preKeyState
}

// NewSignalProtocolService creates a new Signal Protocol service with
// freshly-generated X25519 + Ed25519 long-term identity keys.
func NewSignalProtocolService() (*SignalProtocolService, error) {
	ed25519Svc := NewEd25519Service()

	edPriv, edPub, err := ed25519Svc.GenerateKeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate Ed25519 key pair: %w", err)
	}

	xPriv, xPub, err := generateX25519KeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate X25519 identity key pair: %w", err)
	}

	return &SignalProtocolService{
		sessions:           make(map[string]*SignalSession),
		identityX25519Priv: xPriv,
		identityX25519Pub:  xPub,
		ed25519PrivateKey:  edPriv,
		ed25519PublicKey:   edPub,
		ed25519Service:     ed25519Svc,
		preKeys: preKeyState{
			oneTimePreKeys: make(map[int32]oneTimePreKey),
		},
	}, nil
}

// SetLocalUhid sets the local node's UHID. Required before any Encrypt call
// so the SenderUhid is correctly stamped. GeneratePreKeyBundle also captures
// this automatically.
func (sps *SignalProtocolService) SetLocalUhid(uhid string) {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	sps.localUhid = uhid
}

// HasSession returns true if an active session exists with the given peer.
func (sps *SignalProtocolService) HasSession(peerUhid string) bool {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	_, ok := sps.sessions[peerUhid]
	return ok
}

// Encrypt encrypts plaintext for a peer using the session's sending chain.
// The first message after initiator-side X3DH is returned with
// MessageType=PreKey and carries the X3DH inputs the responder needs to
// derive the same root key.
func (sps *SignalProtocolService) Encrypt(peerUhid string, plaintext []byte) (*EncryptedPayload, error) {
	if plaintext == nil {
		return nil, fmt.Errorf("plaintext cannot be nil")
	}

	sps.mu.Lock()
	defer sps.mu.Unlock()

	session, ok := sps.sessions[peerUhid]
	if !ok {
		return nil, fmt.Errorf("no session established with peer %s", peerUhid)
	}
	if sps.localUhid == "" {
		return nil, fmt.Errorf("local UHID is not set; call GeneratePreKeyBundle or SetLocalUhid before encrypting")
	}

	newChain, msgKey, err := ratchetChainKey(session.SendChainKey)
	if err != nil {
		return nil, fmt.Errorf("ratchet chain key: %w", err)
	}
	session.SendChainKey = newChain
	defer ZeroMemory(msgKey)

	nonce := make([]byte, aesNonceSize)
	if _, err := rand.Read(nonce); err != nil {
		return nil, fmt.Errorf("rand.Read nonce: %w", err)
	}

	ct, err := aesGcmSeal(msgKey, nonce, plaintext)
	if err != nil {
		return nil, err
	}

	counter := session.SendCounter
	session.SendCounter++

	payload := &EncryptedPayload{
		Ciphertext:  ct,
		Nonce:       nonce,
		MessageType: MessageTypeNormal,
		SenderUhid:  sps.localUhid,
		Counter:     counter,
	}

	// First message after initiator-side X3DH? Carry the inputs the
	// responder needs to mirror the DHs.
	if session.PendingPreKeyMessage {
		payload.MessageType = MessageTypePreKey
		payload.InitiatorIdentityKeyX25519 = append([]byte{}, session.InitiatorIdentityKeyX25519...)
		payload.InitiatorEphemeralKeyX25519 = append([]byte{}, session.InitiatorEphemeralKeyX25519...)
		payload.UsedSignedPreKeyID = session.UsedSignedPreKeyID
		payload.UsedOneTimePreKeyID = session.UsedOneTimePreKeyID
		session.PendingPreKeyMessage = false
	}

	return payload, nil
}

// Decrypt decrypts an encrypted payload from a peer. If MessageType=PreKey
// and no session exists yet, the responder-side session is established
// first via mirrored X3DH.
func (sps *SignalProtocolService) Decrypt(peerUhid string, payload *EncryptedPayload) ([]byte, error) {
	if payload == nil {
		return nil, fmt.Errorf("payload cannot be nil")
	}

	sps.mu.Lock()
	defer sps.mu.Unlock()

	if payload.MessageType == MessageTypePreKey {
		if len(payload.InitiatorIdentityKeyX25519) == 0 || len(payload.InitiatorEphemeralKeyX25519) == 0 {
			return nil, fmt.Errorf("PreKey message missing initiator key material")
		}
		if err := sps.establishResponderSessionLocked(peerUhid, payload); err != nil {
			return nil, fmt.Errorf("establish responder session: %w", err)
		}
	}

	session, ok := sps.sessions[peerUhid]
	if !ok {
		return nil, fmt.Errorf("no session established with peer %s", peerUhid)
	}

	if len(payload.Ciphertext) < aesTagSize {
		return nil, fmt.Errorf("ciphertext too short")
	}

	// Skipped key cache?
	if skippedKey, ok := session.SkippedMessageKeys[payload.Counter]; ok {
		delete(session.SkippedMessageKeys, payload.Counter)
		defer ZeroMemory(skippedKey)
		return aesGcmOpen(skippedKey, payload.Nonce, payload.Ciphertext)
	}

	gap := payload.Counter - session.RecvCounter
	if gap > MaxSkippedKeys {
		return nil, fmt.Errorf("message counter gap (%d) exceeds maximum (%d), session must be re-established", gap, MaxSkippedKeys)
	}

	// Skip ahead and cache intermediate keys.
	for session.RecvCounter < payload.Counter {
		newChain, skip, err := ratchetChainKey(session.RecvChainKey)
		if err != nil {
			return nil, fmt.Errorf("ratchet skip key: %w", err)
		}
		session.RecvChainKey = newChain
		session.SkippedMessageKeys[session.RecvCounter] = skip
		session.RecvCounter++
	}

	newChain, msgKey, err := ratchetChainKey(session.RecvChainKey)
	if err != nil {
		return nil, fmt.Errorf("ratchet message key: %w", err)
	}
	session.RecvChainKey = newChain
	session.RecvCounter++
	defer ZeroMemory(msgKey)

	return aesGcmOpen(msgKey, payload.Nonce, payload.Ciphertext)
}

// GeneratePreKeyBundle generates a pre-key bundle for this node. The
// private halves of the signed pre-key and one-time pre-key are retained
// internally so we can run our side of X3DH when an initiator consumes
// these ids.
func (sps *SignalProtocolService) GeneratePreKeyBundle(localUhid string) (*PreKeyBundle, error) {
	sps.mu.Lock()
	defer sps.mu.Unlock()

	sps.localUhid = localUhid

	// One-time pre-key (X25519).
	otpkPriv, otpkPub, err := generateX25519KeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate one-time pre-key: %w", err)
	}
	preKeyID, err := randomInt32()
	if err != nil {
		return nil, err
	}
	sps.preKeys.oneTimePreKeys[preKeyID] = oneTimePreKey{priv: otpkPriv, pub: otpkPub}

	// Signed pre-key (X25519). The signature is over the X25519 public key,
	// signed by the long-term Ed25519 identity key.
	spkPriv, spkPub, err := generateX25519KeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate signed pre-key: %w", err)
	}
	signedPreKeyID, err := randomInt32()
	if err != nil {
		return nil, err
	}
	signature, err := sps.ed25519Service.Sign(sps.ed25519PrivateKey, spkPub)
	if err != nil {
		return nil, fmt.Errorf("sign pre-key: %w", err)
	}
	sps.preKeys.signedPreKeyID = signedPreKeyID
	sps.preKeys.signedPreKeyPriv = spkPriv
	sps.preKeys.signedPreKeyPub = spkPub
	sps.preKeys.signedPreKeySignature = signature

	return &PreKeyBundle{
		Uhid:                  localUhid,
		IdentityKey:           append([]byte{}, sps.ed25519PublicKey...),
		IdentityKeyX25519:     append([]byte{}, sps.identityX25519Pub...),
		PreKeyID:              preKeyID,
		PreKey:                append([]byte{}, otpkPub...),
		SignedPreKeyID:        signedPreKeyID,
		SignedPreKey:          append([]byte{}, spkPub...),
		SignedPreKeySignature: signature,
	}, nil
}

// ProcessPreKeyBundle establishes an initiator-side session against the
// supplied bundle via X3DH (Signal §3.3). The first Encrypt call after this
// returns a PreKey message carrying the inputs the responder needs.
func (sps *SignalProtocolService) ProcessPreKeyBundle(bundle *PreKeyBundle) error {
	if bundle == nil {
		return fmt.Errorf("bundle cannot be nil")
	}
	if len(bundle.IdentityKeyX25519) != X25519PublicKeySize {
		return fmt.Errorf("bundle has malformed X25519 identity key (length %d, want %d)", len(bundle.IdentityKeyX25519), X25519PublicKeySize)
	}
	if len(bundle.SignedPreKey) != X25519PublicKeySize {
		return fmt.Errorf("bundle has malformed signed pre-key (length %d, want %d)", len(bundle.SignedPreKey), X25519PublicKeySize)
	}
	if len(bundle.PreKey) != X25519PublicKeySize {
		return fmt.Errorf("bundle has malformed one-time pre-key (length %d, want %d)", len(bundle.PreKey), X25519PublicKeySize)
	}

	// Verify the signed pre-key signature with the bundle's Ed25519 identity key.
	if !sps.ed25519Service.Verify(bundle.IdentityKey, bundle.SignedPreKey, bundle.SignedPreKeySignature) {
		return fmt.Errorf("signed pre-key signature verification failed")
	}

	// Fresh ephemeral X25519 keypair, generated per-session per Signal §3.3.
	ekPriv, ekPub, err := generateX25519KeyPair()
	if err != nil {
		return fmt.Errorf("generate ephemeral key: %w", err)
	}
	defer ZeroMemory(ekPriv)

	// X3DH 4-DH key agreement (initiator side).
	dh1, err := x25519Agree(sps.identityX25519Priv, bundle.SignedPreKey)
	if err != nil {
		return fmt.Errorf("DH1: %w", err)
	}
	defer ZeroMemory(dh1)
	dh2, err := x25519Agree(ekPriv, bundle.IdentityKeyX25519)
	if err != nil {
		return fmt.Errorf("DH2: %w", err)
	}
	defer ZeroMemory(dh2)
	dh3, err := x25519Agree(ekPriv, bundle.SignedPreKey)
	if err != nil {
		return fmt.Errorf("DH3: %w", err)
	}
	defer ZeroMemory(dh3)
	dh4, err := x25519Agree(ekPriv, bundle.PreKey)
	if err != nil {
		return fmt.Errorf("DH4: %w", err)
	}
	defer ZeroMemory(dh4)

	sharedSecret := concat(dh1, dh2, dh3, dh4)
	defer ZeroMemory(sharedSecret)

	rootKey := deriveKey(sharedSecret, hkdfRootInfo)
	sendChain := deriveKey(rootKey, hkdfChainInitiatorSendInfo)
	recvChain := deriveKey(rootKey, hkdfChainInitiatorRecvInfo)

	session := &SignalSession{
		RootKey:                     rootKey,
		SendChainKey:                sendChain,
		RecvChainKey:                recvChain,
		SkippedMessageKeys:          make(map[int32][]byte),
		PendingPreKeyMessage:        true,
		InitiatorIdentityKeyX25519:  append([]byte{}, sps.identityX25519Pub...),
		InitiatorEphemeralKeyX25519: append([]byte{}, ekPub...),
		UsedSignedPreKeyID:          bundle.SignedPreKeyID,
		UsedOneTimePreKeyID:         bundle.PreKeyID,
	}

	sps.mu.Lock()
	sps.sessions[bundle.Uhid] = session
	sps.mu.Unlock()

	return nil
}

// establishResponderSessionLocked mirrors the initiator's 4 X3DH DHs to
// derive the same root key, then derives chain keys with send/recv SWAPPED
// relative to the initiator. Consumes (and zeros) the one-time pre-key.
//
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) establishResponderSessionLocked(peerUhid string, payload *EncryptedPayload) error {
	if len(payload.InitiatorIdentityKeyX25519) != X25519PublicKeySize {
		return fmt.Errorf("initiator IK_X25519 has wrong size: %d (want %d)", len(payload.InitiatorIdentityKeyX25519), X25519PublicKeySize)
	}
	if len(payload.InitiatorEphemeralKeyX25519) != X25519PublicKeySize {
		return fmt.Errorf("initiator EK_X25519 has wrong size: %d (want %d)", len(payload.InitiatorEphemeralKeyX25519), X25519PublicKeySize)
	}
	if sps.preKeys.signedPreKeyID != payload.UsedSignedPreKeyID || len(sps.preKeys.signedPreKeyPriv) == 0 {
		return fmt.Errorf("PreKey message references signed pre-key id %d which is not held by this node", payload.UsedSignedPreKeyID)
	}
	otpk, ok := sps.preKeys.oneTimePreKeys[payload.UsedOneTimePreKeyID]
	if !ok {
		return fmt.Errorf("PreKey message references one-time pre-key id %d which is not held (already consumed?)", payload.UsedOneTimePreKeyID)
	}

	// Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
	dh1, err := x25519Agree(sps.preKeys.signedPreKeyPriv, payload.InitiatorIdentityKeyX25519)
	if err != nil {
		return fmt.Errorf("DH1': %w", err)
	}
	defer ZeroMemory(dh1)
	dh2, err := x25519Agree(sps.identityX25519Priv, payload.InitiatorEphemeralKeyX25519)
	if err != nil {
		return fmt.Errorf("DH2': %w", err)
	}
	defer ZeroMemory(dh2)
	dh3, err := x25519Agree(sps.preKeys.signedPreKeyPriv, payload.InitiatorEphemeralKeyX25519)
	if err != nil {
		return fmt.Errorf("DH3': %w", err)
	}
	defer ZeroMemory(dh3)
	dh4, err := x25519Agree(otpk.priv, payload.InitiatorEphemeralKeyX25519)
	if err != nil {
		return fmt.Errorf("DH4': %w", err)
	}
	defer ZeroMemory(dh4)

	sharedSecret := concat(dh1, dh2, dh3, dh4)
	defer ZeroMemory(sharedSecret)

	rootKey := deriveKey(sharedSecret, hkdfRootInfo)
	// SWAPPED: initiator's send-chain info derives our recv-chain (and vice versa).
	recvChain := deriveKey(rootKey, hkdfChainInitiatorSendInfo)
	sendChain := deriveKey(rootKey, hkdfChainInitiatorRecvInfo)

	sps.sessions[peerUhid] = &SignalSession{
		RootKey:            rootKey,
		SendChainKey:       sendChain,
		RecvChainKey:       recvChain,
		SkippedMessageKeys: make(map[int32][]byte),
	}

	// Consume one-time pre-key.
	ZeroMemory(otpk.priv)
	delete(sps.preKeys.oneTimePreKeys, payload.UsedOneTimePreKeyID)

	return nil
}

// SignData signs data using the local Ed25519 private key.
func (sps *SignalProtocolService) SignData(data []byte) ([]byte, error) {
	if data == nil {
		return nil, fmt.Errorf("data cannot be nil")
	}
	return sps.ed25519Service.Sign(sps.ed25519PrivateKey, data)
}

// VerifySignature verifies an Ed25519 signature.
func (sps *SignalProtocolService) VerifySignature(publicKey, data, signature []byte) bool {
	return sps.ed25519Service.Verify(publicKey, data, signature)
}

// GetPublicKey returns a copy of the Ed25519 public key.
func (sps *SignalProtocolService) GetPublicKey() []byte {
	return append([]byte{}, sps.ed25519PublicKey...)
}

// GetX25519PublicKey returns a copy of the X25519 ECDH public key.
func (sps *SignalProtocolService) GetX25519PublicKey() []byte {
	return append([]byte{}, sps.identityX25519Pub...)
}

// deriveKey derives a 32-byte key from input key material using HKDF-SHA256.
func deriveKey(ikm []byte, info []byte) []byte {
	h := hkdf.New(sha256.New, ikm, nil, info)
	key := make([]byte, aesKeySize)
	if _, err := h.Read(key); err != nil {
		panic(err) // HKDF over fixed inputs cannot fail.
	}
	return key
}

// ratchetChainKey advances a chain key by one step per Signal §5.1.
func ratchetChainKey(chainKey []byte) (newChain, messageKey []byte, err error) {
	h1 := hmac.New(sha256.New, chainKey)
	h1.Write([]byte{0x01})
	messageKey = h1.Sum(nil)

	h2 := hmac.New(sha256.New, chainKey)
	h2.Write([]byte{0x02})
	newChain = h2.Sum(nil)
	return newChain, messageKey, nil
}

func aesGcmSeal(key, nonce, plaintext []byte) ([]byte, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, fmt.Errorf("aes.NewCipher: %w", err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("cipher.NewGCM: %w", err)
	}
	return gcm.Seal(nil, nonce, plaintext, nil), nil
}

func aesGcmOpen(key, nonce, ciphertext []byte) ([]byte, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, fmt.Errorf("aes.NewCipher: %w", err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("cipher.NewGCM: %w", err)
	}
	return gcm.Open(nil, nonce, ciphertext, nil)
}

func concat(arrays ...[]byte) []byte {
	total := 0
	for _, a := range arrays {
		total += len(a)
	}
	out := make([]byte, 0, total)
	for _, a := range arrays {
		out = append(out, a...)
	}
	return out
}

// randomInt32 generates a random non-zero int32 in [1, 2^31-1].
func randomInt32() (int32, error) {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return 0, err
	}
	r := int32((uint32(b[0])<<24 | uint32(b[1])<<16 | uint32(b[2])<<8 | uint32(b[3])) & 0x7FFFFFFF)
	if r == 0 {
		r = 1
	}
	return r, nil
}

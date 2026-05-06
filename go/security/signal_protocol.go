// SPDX-License-Identifier: MIT

package security

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"fmt"
	"log"
	"sync"
	"time"

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

	// DefaultOpkPoolSize is the default size of the one-time pre-key pool.
	// Mirrors Signal's published guidance and the C# reference: ~100 OPKs
	// per device so realistic concurrent-initiator loads don't collide on
	// a single shared id (the prior single-OPK design is unsafe under
	// concurrent X3DH establishment).
	DefaultOpkPoolSize = 100

	// maxOpkIdAllocAttempts caps the number of retries when picking a
	// non-colliding random OPK id. RandomNumberGenerator-grade collisions
	// in a 100-element pool are statistically negligible, but guarding
	// explicitly turns a degenerate RNG failure into a clear error rather
	// than an infinite loop.
	maxOpkIdAllocAttempts = 64
)

// HKDF info strings.
//
// hkdfRootInfo is used by the X3DH initial root-key derivation (Signal §3).
// hkdfRatchetInfo is used by KDF_RK in the DH-ratchet step (Signal §5.2):
// salt=rootKey, ikm=DH(DHs, DHr), 64-byte output split into
// (newRootKey[0..32], newChainKey[32..64]).
//
// hkdfChainInitiatorSendInfo and hkdfChainInitiatorRecvInfo are retained
// only for the cross-language fixture verifier (`signal_fixture_test.go`)
// — they are NOT used by the live Double-Ratchet code path. The
// fixture pins HKDF derivation math against these labels so any drift
// between language implementations shows up as a hex mismatch.
//
// All info strings MUST match the C# reference (`SignalProtocolService.cs`)
// byte-for-byte.
var (
	hkdfRootInfo               = []byte("aether-x3dh-root-v1")
	hkdfRatchetInfo            = []byte("aether-ratchet-rk-v1")
	hkdfChainInitiatorSendInfo = []byte("aether-chain-initiator-send-v1")
	hkdfChainInitiatorRecvInfo = []byte("aether-chain-initiator-recv-v1")
)

// SignalProtocolService implements X3DH + full Double-Ratchet (Signal §5)
// for end-to-end encryption.
//
// Key agreement: X3DH (Signal §3) over X25519 (RFC 7748). Four DHs:
//   - DH1 = DH(IK_A, SPK_B) — long-term mutual auth
//   - DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
//   - DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
//   - DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
//
// Initial root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
//
// Double Ratchet (§5): each side maintains a current X25519 ratchet
// keypair. When the receiver sees a peer message with a new ratchet
// public key, it does a DH-ratchet step: derive a new chain key via
// KDF_RK(RK, DH(myDHs_priv, newDHr)), then rotate to a fresh DHs and
// derive its sending chain via KDF_RK(RK, DH(newDHs_priv, newDHr)).
// Signal-canonical X3DH↔Double-Ratchet integration: the initiator's
// X3DH ephemeral key becomes its first DH-ratchet keypair.
//
// Symmetric ratchet (§5.1): HMAC-SHA256, single-byte domain separation
//
//	(0x01 → message key, 0x02 → next chain key).
//
// Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
// Identity signing: Ed25519.
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

	// opkPoolSize is the target size of the one-time pre-key pool. The
	// pool is topped up to this many available (un-issued) keys on every
	// bundle generation. Mirrors the C# OpkPoolSize. Defaults to
	// DefaultOpkPoolSize (100); override via WithOpkPoolSize.
	opkPoolSize int

	// Persistence stores. Nil means no persistence (in-memory only).
	sessionStore ISignalSessionStore
	preKeyStore  IPreKeyStore

	// Signed-pre-key rotation policy. Defaults to
	// DefaultSignedPreKeyRotationOptions (7-day rotation, 3 retained).
	rotationOptions SignedPreKeyRotationOptions

	// nowProvider is the clock for SPK rotation. Override via WithNowProvider
	// for tests that need a synthetic clock. Defaults to time.Now.
	nowProvider func() time.Time

	// logger receives best-effort warnings for persistence failures.
	logger *log.Logger
}

// SignalOption configures a SignalProtocolService at construction time.
type SignalOption func(*SignalProtocolService) error

// WithOpkPoolSize overrides the target size of the one-time pre-key pool.
// Default is DefaultOpkPoolSize (100). Must be >= 1.
//
// Larger pools reduce the chance of bundle-issuance collisions under
// concurrent initiator load at the cost of slightly more X25519 keypair
// generation work per node and persistent OPK storage.
func WithOpkPoolSize(size int) SignalOption {
	return func(s *SignalProtocolService) error {
		if size < 1 {
			return fmt.Errorf("WithOpkPoolSize: size must be >= 1 (got %d)", size)
		}
		s.opkPoolSize = size
		return nil
	}
}

// WithSessionStore wires a persistent ISignalSessionStore into the service.
// On construction, every previously-stored session is hydrated. After
// every encrypt / decrypt mutation, the affected session is saved
// (best-effort: failures are logged via the configured logger and do not
// abort the message flow).
//
// Mirrors the C# SignalProtocolService(sessionStore: …) constructor
// parameter.
func WithSessionStore(store ISignalSessionStore) SignalOption {
	return func(s *SignalProtocolService) error {
		s.sessionStore = store
		return nil
	}
}

// WithPreKeyStore wires a persistent IPreKeyStore into the service. On
// construction, the long-term identity keys, signed-pre-key history, and
// one-time pre-key pool are hydrated from the store (or generated and
// saved if no prior state exists). After every state mutation, the
// affected slice is saved (best-effort).
//
// Mirrors the C# SignalProtocolService(preKeyStore: …) constructor
// parameter.
func WithPreKeyStore(store IPreKeyStore) SignalOption {
	return func(s *SignalProtocolService) error {
		s.preKeyStore = store
		return nil
	}
}

// WithRotationOptions sets the signed-pre-key rotation policy. Default is
// DefaultSignedPreKeyRotationOptions (7-day interval, 3 retained prior
// entries). Must have RotationInterval > 0 and RetainedHistoryCount >= 0.
//
// Mirrors the C# SignedPreKeyRotationOptions constructor parameter.
func WithRotationOptions(opts SignedPreKeyRotationOptions) SignalOption {
	return func(s *SignalProtocolService) error {
		if opts.RotationInterval <= 0 {
			return fmt.Errorf("WithRotationOptions: RotationInterval must be > 0 (got %v)", opts.RotationInterval)
		}
		if opts.RetainedHistoryCount < 0 {
			return fmt.Errorf("WithRotationOptions: RetainedHistoryCount must be >= 0 (got %d)", opts.RetainedHistoryCount)
		}
		s.rotationOptions = opts
		return nil
	}
}

// WithNowProvider injects a synthetic clock for SPK rotation tests.
// Default is time.Now. Mirrors the C# nowProvider parameter.
func WithNowProvider(now func() time.Time) SignalOption {
	return func(s *SignalProtocolService) error {
		if now == nil {
			return fmt.Errorf("WithNowProvider: now cannot be nil")
		}
		s.nowProvider = now
		return nil
	}
}

// WithLogger injects a *log.Logger for persistence-failure warnings.
// Defaults to a stderr logger. Pass log.New(io.Discard, …) to silence.
func WithLogger(l *log.Logger) SignalOption {
	return func(s *SignalProtocolService) error {
		s.logger = l
		return nil
	}
}

// NewSignalProtocolService creates a new Signal Protocol service with
// freshly-generated X25519 + Ed25519 long-term identity keys. Optional
// behaviour (OPK pool size, persistence, SPK rotation policy, synthetic
// clock) can be configured via SignalOption.
//
// When WithPreKeyStore is supplied and the store has prior state, the
// identity keys, signed-pre-key history, and OPK pool are loaded from the
// store (overriding the freshly-generated identity). When the store is
// empty, the freshly-generated identity is saved.
//
// When WithSessionStore is supplied, every previously-stored session is
// hydrated on construction.
func NewSignalProtocolService(opts ...SignalOption) (*SignalProtocolService, error) {
	ed25519Svc := NewEd25519Service()

	edPriv, edPub, err := ed25519Svc.GenerateKeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate Ed25519 key pair: %w", err)
	}

	xPriv, xPub, err := generateX25519KeyPair()
	if err != nil {
		return nil, fmt.Errorf("generate X25519 identity key pair: %w", err)
	}

	sps := &SignalProtocolService{
		sessions:           make(map[string]*SignalSession),
		identityX25519Priv: xPriv,
		identityX25519Pub:  xPub,
		ed25519PrivateKey:  edPriv,
		ed25519PublicKey:   edPub,
		ed25519Service:     ed25519Svc,
		preKeys: preKeyState{
			oneTimePreKeys:      make(map[int32]oneTimePreKey),
			availableOpkIds:     make([]int32, 0),
			signedPreKeyHistory: make([]signedPreKeyEntry, 0),
		},
		opkPoolSize:     DefaultOpkPoolSize,
		rotationOptions: DefaultSignedPreKeyRotationOptions(),
		nowProvider:     time.Now,
		logger:          log.Default(),
	}
	for _, opt := range opts {
		if err := opt(sps); err != nil {
			return nil, err
		}
	}

	// Hydrate identity / pre-keys from the pre-key store if one is wired in.
	if sps.preKeyStore != nil {
		if err := sps.hydrateFromPreKeyStore(); err != nil {
			sps.logger.Printf("SignalProtocolService: hydrateFromPreKeyStore: %v (continuing with freshly-generated keys)", err)
		}
	}

	// Hydrate sessions from the session store if one is wired in. Done
	// AFTER pre-key hydration so the identity keys match what the sessions
	// were established under.
	if sps.sessionStore != nil {
		if err := sps.hydrateFromSessionStore(); err != nil {
			sps.logger.Printf("SignalProtocolService: hydrateFromSessionStore: %v", err)
		}
	}

	return sps, nil
}

// hydrateFromPreKeyStore loads identity, SPK history, and OPK pool from
// the pre-key store. If the store has no prior identity, the freshly-
// generated keys are saved instead.
//
// Caller must NOT hold sps.mu — this runs at construction before the
// service is exposed.
func (sps *SignalProtocolService) hydrateFromPreKeyStore() error {
	ctx := context.Background()
	store := sps.preKeyStore

	stored, err := store.LoadIdentity(ctx)
	if err != nil {
		return fmt.Errorf("LoadIdentity: %w", err)
	}
	if stored != nil {
		sps.ed25519PrivateKey = stored.Ed25519PrivateKey
		sps.ed25519PublicKey = stored.Ed25519PublicKey
		sps.identityX25519Priv = stored.X25519PrivateKey
		sps.identityX25519Pub = stored.X25519PublicKey
		if stored.LocalUhid != "" {
			sps.localUhid = stored.LocalUhid
		}
	} else {
		fresh := &StoredIdentityKeys{
			Ed25519PrivateKey: sps.ed25519PrivateKey,
			Ed25519PublicKey:  sps.ed25519PublicKey,
			X25519PrivateKey:  sps.identityX25519Priv,
			X25519PublicKey:   sps.identityX25519Pub,
			LocalUhid:         sps.localUhid,
		}
		if err := store.SaveIdentity(ctx, fresh); err != nil {
			return fmt.Errorf("SaveIdentity (initial): %w", err)
		}
	}

	history, err := store.LoadSignedPreKeys(ctx)
	if err != nil {
		return fmt.Errorf("LoadSignedPreKeys: %w", err)
	}
	sps.preKeys.signedPreKeyHistory = sps.preKeys.signedPreKeyHistory[:0]
	for _, e := range history.Entries {
		sps.preKeys.signedPreKeyHistory = append(sps.preKeys.signedPreKeyHistory, signedPreKeyEntry{
			id:          e.ID,
			priv:        e.PrivateKey,
			pub:         e.PublicKey,
			signature:   e.Signature,
			generatedAt: e.GeneratedAt,
		})
	}
	if n := len(sps.preKeys.signedPreKeyHistory); n > 0 {
		active := sps.preKeys.signedPreKeyHistory[n-1]
		sps.preKeys.signedPreKeyID = active.id
		sps.preKeys.signedPreKeyPriv = active.priv
		sps.preKeys.signedPreKeyPub = active.pub
		sps.preKeys.signedPreKeySignature = active.signature
	}

	opks, err := store.LoadOneTimePreKeys(ctx)
	if err != nil {
		return fmt.Errorf("LoadOneTimePreKeys: %w", err)
	}
	sps.preKeys.oneTimePreKeys = make(map[int32]oneTimePreKey, len(opks))
	sps.preKeys.availableOpkIds = sps.preKeys.availableOpkIds[:0]
	for id, opk := range opks {
		sps.preKeys.oneTimePreKeys[id] = oneTimePreKey{priv: opk.PrivateKey, pub: opk.PublicKey}
		if !opk.Issued {
			sps.preKeys.availableOpkIds = append(sps.preKeys.availableOpkIds, id)
		}
	}
	return nil
}

// hydrateFromSessionStore loads every previously-stored session.
// Failures on individual peers log a warning and the loop continues —
// one bad session shouldn't lose every session.
func (sps *SignalProtocolService) hydrateFromSessionStore() error {
	ctx := context.Background()
	peers, err := sps.sessionStore.ListPeers(ctx)
	if err != nil {
		return fmt.Errorf("ListPeers: %w", err)
	}
	for _, peerUhid := range peers {
		session, lerr := sps.sessionStore.LoadSession(ctx, peerUhid)
		if lerr != nil {
			sps.logger.Printf("SignalProtocolService: failed to load session for %s: %v", peerUhid, lerr)
			continue
		}
		if session != nil {
			sps.sessions[peerUhid] = session
		}
	}
	return nil
}

// tryPersistSessionLocked best-effort saves a session. Failures log but do not abort.
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) tryPersistSessionLocked(peerUhid string, session *SignalSession) {
	if sps.sessionStore == nil {
		return
	}
	if err := sps.sessionStore.SaveSession(context.Background(), peerUhid, session); err != nil {
		sps.logger.Printf("SignalProtocolService: failed to persist session for %s: %v", peerUhid, err)
	}
}

// tryPersistIdentityLocked best-effort saves identity keys.
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) tryPersistIdentityLocked() {
	if sps.preKeyStore == nil {
		return
	}
	snap := &StoredIdentityKeys{
		Ed25519PrivateKey: sps.ed25519PrivateKey,
		Ed25519PublicKey:  sps.ed25519PublicKey,
		X25519PrivateKey:  sps.identityX25519Priv,
		X25519PublicKey:   sps.identityX25519Pub,
		LocalUhid:         sps.localUhid,
	}
	if err := sps.preKeyStore.SaveIdentity(context.Background(), snap); err != nil {
		sps.logger.Printf("SignalProtocolService: failed to persist identity keys: %v", err)
	}
}

// tryPersistSignedPreKeysLocked best-effort saves the SPK history.
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) tryPersistSignedPreKeysLocked() {
	if sps.preKeyStore == nil {
		return
	}
	hist := StoredSignedPreKeyHistory{Entries: make([]StoredSignedPreKey, 0, len(sps.preKeys.signedPreKeyHistory))}
	for _, e := range sps.preKeys.signedPreKeyHistory {
		hist.Entries = append(hist.Entries, StoredSignedPreKey{
			ID:          e.id,
			PrivateKey:  e.priv,
			PublicKey:   e.pub,
			Signature:   e.signature,
			GeneratedAt: e.generatedAt,
		})
	}
	if err := sps.preKeyStore.SaveSignedPreKeys(context.Background(), hist); err != nil {
		sps.logger.Printf("SignalProtocolService: failed to persist SPK history: %v", err)
	}
}

// tryPersistOneTimePreKeysLocked best-effort saves the OPK pool.
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) tryPersistOneTimePreKeysLocked() {
	if sps.preKeyStore == nil {
		return
	}
	// "Issued" = present in oneTimePreKeys but NOT queued in availableOpkIds.
	issued := make(map[int32]struct{}, len(sps.preKeys.oneTimePreKeys))
	for id := range sps.preKeys.oneTimePreKeys {
		issued[id] = struct{}{}
	}
	for _, id := range sps.preKeys.availableOpkIds {
		delete(issued, id)
	}
	pool := make(map[int32]StoredOneTimePreKey, len(sps.preKeys.oneTimePreKeys))
	for id, opk := range sps.preKeys.oneTimePreKeys {
		_, isIssued := issued[id]
		pool[id] = StoredOneTimePreKey{
			ID:         id,
			PrivateKey: opk.priv,
			PublicKey:  opk.pub,
			Issued:     isIssued,
		}
	}
	if err := sps.preKeyStore.SaveOneTimePreKeys(context.Background(), pool); err != nil {
		sps.logger.Printf("SignalProtocolService: failed to persist OPK pool: %v", err)
	}
}

// tryConsumeOneTimePreKeyLocked best-effort removes a single OPK from the
// pre-key store. Caller MUST hold sps.mu.
func (sps *SignalProtocolService) tryConsumeOneTimePreKeyLocked(id int32) {
	if sps.preKeyStore == nil {
		return
	}
	if err := sps.preKeyStore.ConsumeOneTimePreKey(context.Background(), id); err != nil {
		sps.logger.Printf("SignalProtocolService: failed to consume OPK %d: %v", id, err)
	}
}

// SetLocalUhid sets the local node's UHID. Required before any Encrypt call
// so the SenderUhid is correctly stamped. GeneratePreKeyBundle also captures
// this automatically. If a pre-key store is wired in, the identity record
// is re-persisted with the new UHID.
func (sps *SignalProtocolService) SetLocalUhid(uhid string) {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	sps.localUhid = uhid
	sps.tryPersistIdentityLocked()
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
// derive the same root key. Every message — PreKey or normal — carries
// the sender's current DH-ratchet public key (SenderEphemeralKeyX25519)
// and previous-chain message count (PreviousChainCount).
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

	// Lazy CKs initialization for the initiator's first send. The X3DH
	// setup placed DHs and DHr but did not derive CKs — the Double Ratchet
	// defers it until first send to avoid an extra KDF step when no
	// message is ever sent on a session.
	if session.SendChainKey == nil {
		if session.RemoteEphemeralPub == nil {
			return nil, fmt.Errorf("cannot derive sending chain: peer's ratchet public key is unknown")
		}
		if err := dhRatchetSendOnly(session, session.RemoteEphemeralPub); err != nil {
			return nil, fmt.Errorf("dh-ratchet send-only init: %w", err)
		}
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

	ratchetPub := append([]byte{}, session.MyEphemeralPub...)

	payload := &EncryptedPayload{
		Ciphertext:               ct,
		Nonce:                    nonce,
		MessageType:              MessageTypeNormal,
		SenderUhid:               sps.localUhid,
		Counter:                  counter,
		SenderEphemeralKeyX25519: ratchetPub,
		PreviousChainCount:       session.PreviousChainCount,
	}

	// First message after initiator-side X3DH? Carry the inputs the
	// responder needs to mirror the DHs. Backward-compat: also populate
	// InitiatorEphemeralKeyX25519 with the same value as
	// SenderEphemeralKeyX25519 (older peers may only read the legacy field).
	if session.PendingPreKeyMessage {
		payload.MessageType = MessageTypePreKey
		payload.InitiatorIdentityKeyX25519 = append([]byte{}, session.InitiatorIdentityKeyX25519...)
		payload.InitiatorEphemeralKeyX25519 = append([]byte{}, ratchetPub...)
		payload.UsedSignedPreKeyID = session.UsedSignedPreKeyID
		payload.UsedOneTimePreKeyID = session.UsedOneTimePreKeyID
		session.PendingPreKeyMessage = false
	}

	// Persist the mutated session (best-effort; logged on failure).
	sps.tryPersistSessionLocked(peerUhid, session)

	return payload, nil
}

// Decrypt decrypts an encrypted payload from a peer. If MessageType=PreKey
// and no session exists yet, the responder-side session is established
// first via mirrored X3DH. Every message triggers a DH-ratchet step on the
// receiver side when the sender's ratchet public key changes.
func (sps *SignalProtocolService) Decrypt(peerUhid string, payload *EncryptedPayload) ([]byte, error) {
	if payload == nil {
		return nil, fmt.Errorf("payload cannot be nil")
	}

	sps.mu.Lock()
	defer sps.mu.Unlock()

	// Every Double-Ratchet message carries the sender's current ratchet
	// public key. Fall back to InitiatorEphemeralKeyX25519 for backward
	// compatibility with older PreKey messages from peers that haven't
	// upgraded to the new wire envelope.
	senderRatchetPub := payload.SenderEphemeralKeyX25519
	if senderRatchetPub == nil {
		senderRatchetPub = payload.InitiatorEphemeralKeyX25519
	}

	if payload.MessageType == MessageTypePreKey {
		if len(payload.InitiatorIdentityKeyX25519) == 0 || len(senderRatchetPub) == 0 {
			return nil, fmt.Errorf("PreKey message missing initiator key material (InitiatorIdentityKeyX25519 and SenderEphemeralKeyX25519/InitiatorEphemeralKeyX25519)")
		}
		if err := sps.establishResponderSessionLocked(peerUhid, payload, senderRatchetPub); err != nil {
			return nil, fmt.Errorf("establish responder session: %w", err)
		}
	}

	session, ok := sps.sessions[peerUhid]
	if !ok {
		return nil, fmt.Errorf("no session established with peer %s", peerUhid)
	}

	if len(senderRatchetPub) == 0 {
		return nil, fmt.Errorf("message missing SenderEphemeralKeyX25519 — required for the Double Ratchet")
	}

	// DH-ratchet step? Triggered when the peer's ratchet public key changes
	// (or first arrives, on the responder side after X3DH establishment).
	if session.RemoteEphemeralPub == nil || !constantTimeEqual(senderRatchetPub, session.RemoteEphemeralPub) {
		// First, derive any skipped keys from the previous receive chain
		// (the chain keyed by the OLD RemoteEphemeralPub). Then ratchet.
		if err := skipMessageKeys(session, payload.PreviousChainCount); err != nil {
			return nil, fmt.Errorf("skip message keys: %w", err)
		}
		if err := dhRatchetReceive(session, senderRatchetPub); err != nil {
			return nil, fmt.Errorf("dh-ratchet receive: %w", err)
		}
	}

	if len(payload.Ciphertext) < aesTagSize {
		return nil, fmt.Errorf("ciphertext too short")
	}

	// Skipped key cached for this (DHr_pub, counter) pair?
	cacheKey := skippedKey(senderRatchetPub, payload.Counter)
	if mk, ok := session.SkippedMessageKeys[cacheKey]; ok {
		delete(session.SkippedMessageKeys, cacheKey)
		defer ZeroMemory(mk)
		plain, err := aesGcmOpen(mk, payload.Nonce, payload.Ciphertext)
		if err == nil {
			sps.tryPersistSessionLocked(peerUhid, session)
		}
		return plain, err
	}

	if session.RecvChainKey == nil {
		return nil, fmt.Errorf("receive chain not initialized (DH-ratchet step missing)")
	}

	gap := payload.Counter - session.RecvCounter
	if gap > MaxSkippedKeys {
		return nil, fmt.Errorf("message counter gap (%d) exceeds maximum (%d), session must be re-established", gap, MaxSkippedKeys)
	}

	// Skip ahead and cache intermediate keys, keyed by the CURRENT remote
	// ratchet pub (which equals senderRatchetPub at this point).
	for session.RecvCounter < payload.Counter {
		newChain, skip, err := ratchetChainKey(session.RecvChainKey)
		if err != nil {
			return nil, fmt.Errorf("ratchet skip key: %w", err)
		}
		session.RecvChainKey = newChain
		session.SkippedMessageKeys[skippedKey(senderRatchetPub, session.RecvCounter)] = skip
		session.RecvCounter++
	}

	newChain, msgKey, err := ratchetChainKey(session.RecvChainKey)
	if err != nil {
		return nil, fmt.Errorf("ratchet message key: %w", err)
	}
	session.RecvChainKey = newChain
	session.RecvCounter++
	defer ZeroMemory(msgKey)

	plain, err := aesGcmOpen(msgKey, payload.Nonce, payload.Ciphertext)
	if err == nil {
		// Persist the mutated session (best-effort).
		sps.tryPersistSessionLocked(peerUhid, session)
	}
	return plain, err
}

// GeneratePreKeyBundle generates a pre-key bundle for this node. The
// private halves of the signed pre-key and one-time pre-key are retained
// internally so we can run our side of X3DH when an initiator consumes
// these ids.
//
// One-time pre-keys are managed as a pool of opkPoolSize entries (default
// 100). Bundle generation:
//
//  1. Tops up the OPK pool to opkPoolSize available (un-issued) keys.
//  2. Dequeues the next un-issued OPK id from the FIFO available queue.
//  3. Returns the bundle. The dequeued OPK stays in oneTimePreKeys until
//     an initiator's PreKey message consumes it via X3DH (zeroed +
//     removed there).
//
// This replaces the prior single-OPK design which had a critical
// concurrency hazard: every bundle reused the same single OPK private key,
// so two concurrent initiators establishing sessions against the same
// responder would both compute X3DH against the same OPK — losing one of
// the two sessions when the responder consumed the OPK on the first
// PreKey message and refused to honour the second.
func (sps *SignalProtocolService) GeneratePreKeyBundle(localUhid string) (*PreKeyBundle, error) {
	sps.mu.Lock()
	defer sps.mu.Unlock()

	uhidChanged := sps.localUhid != localUhid
	sps.localUhid = localUhid
	if uhidChanged {
		sps.tryPersistIdentityLocked()
	}

	// SignedPreKey: generated lazily on the first bundle call. On
	// subsequent calls the active SPK is reused unless its age exceeds
	// RotationOptions.RotationInterval, in which case a fresh SPK is
	// generated and the history is rolled forward. Retained history (default
	// 3 prior entries) lets messages signed under a recently-rotated SPK
	// still complete X3DH during the rotation window — Signal §3.3
	// recommends weekly rotation.
	historyMutated := false
	if len(sps.preKeys.signedPreKeyHistory) == 0 {
		if err := sps.appendNewSignedPreKeyLocked(); err != nil {
			return nil, fmt.Errorf("append initial SPK: %w", err)
		}
		historyMutated = true
	} else {
		active := sps.preKeys.signedPreKeyHistory[len(sps.preKeys.signedPreKeyHistory)-1]
		if sps.nowProvider().Sub(active.generatedAt) >= sps.rotationOptions.RotationInterval {
			if err := sps.appendNewSignedPreKeyLocked(); err != nil {
				return nil, fmt.Errorf("rotate SPK: %w", err)
			}
			historyMutated = true
		}
	}

	active := sps.preKeys.signedPreKeyHistory[len(sps.preKeys.signedPreKeyHistory)-1]
	signedPreKeyID := active.id
	spkPub := active.pub
	signature := active.signature

	// Top up the OPK pool, then dequeue the next un-issued OPK.
	if err := sps.topUpOpkPoolLocked(); err != nil {
		return nil, fmt.Errorf("top up OPK pool: %w", err)
	}
	if len(sps.preKeys.availableOpkIds) == 0 {
		// Defensive — top-up should always leave the queue at >= opkPoolSize.
		return nil, fmt.Errorf("OPK pool unexpectedly empty after top-up")
	}
	preKeyID := sps.preKeys.availableOpkIds[0]
	sps.preKeys.availableOpkIds = sps.preKeys.availableOpkIds[1:]
	otpk, ok := sps.preKeys.oneTimePreKeys[preKeyID]
	if !ok {
		return nil, fmt.Errorf("OPK pool inconsistent: id %d in queue but not in map", preKeyID)
	}
	otpkPub := otpk.pub

	if historyMutated {
		sps.tryPersistSignedPreKeysLocked()
	}
	sps.tryPersistOneTimePreKeysLocked()

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

// appendNewSignedPreKeyLocked generates a fresh SPK, appends it to the
// history as the new active entry, and trims the history to the
// retained-count budget. Caller MUST hold sps.mu.
func (sps *SignalProtocolService) appendNewSignedPreKeyLocked() error {
	spkPriv, spkPub, err := generateX25519KeyPair()
	if err != nil {
		return fmt.Errorf("generate signed pre-key: %w", err)
	}
	id, err := randomInt32()
	if err != nil {
		return fmt.Errorf("allocate SPK id: %w", err)
	}
	sig, err := sps.ed25519Service.Sign(sps.ed25519PrivateKey, spkPub)
	if err != nil {
		ZeroMemory(spkPriv)
		return fmt.Errorf("sign pre-key: %w", err)
	}

	sps.preKeys.signedPreKeyHistory = append(sps.preKeys.signedPreKeyHistory, signedPreKeyEntry{
		id:          id,
		priv:        spkPriv,
		pub:         spkPub,
		signature:   sig,
		generatedAt: sps.nowProvider(),
	})

	// Prune oldest entries beyond the retention budget. Retain at most
	// (1 + RetainedHistoryCount) entries — the new active SPK plus the
	// configured number of retained-prior entries.
	maxEntries := 1 + sps.rotationOptions.RetainedHistoryCount
	for len(sps.preKeys.signedPreKeyHistory) > maxEntries {
		pruned := sps.preKeys.signedPreKeyHistory[0]
		ZeroMemory(pruned.priv)
		sps.preKeys.signedPreKeyHistory = sps.preKeys.signedPreKeyHistory[1:]
	}

	// Update the denormalised mirrors.
	active := sps.preKeys.signedPreKeyHistory[len(sps.preKeys.signedPreKeyHistory)-1]
	sps.preKeys.signedPreKeyID = active.id
	sps.preKeys.signedPreKeyPriv = active.priv
	sps.preKeys.signedPreKeyPub = active.pub
	sps.preKeys.signedPreKeySignature = active.signature
	return nil
}

// findSignedPreKeyLocked walks the SPK history (newest first) for the entry
// with the given id. Returns nil if the id is unknown (rotated out or
// never generated). Caller MUST hold sps.mu.
func (sps *SignalProtocolService) findSignedPreKeyLocked(id int32) *signedPreKeyEntry {
	for i := len(sps.preKeys.signedPreKeyHistory) - 1; i >= 0; i-- {
		if sps.preKeys.signedPreKeyHistory[i].id == id {
			return &sps.preKeys.signedPreKeyHistory[i]
		}
	}
	return nil
}

// RotateSignedPreKey forces a signed-pre-key rotation if the active SPK is
// older than RotationOptions.RotationInterval (or no SPK exists yet).
// Returns true iff a new SPK was generated and persisted.
//
// Mirrors the C# RotateSignedPreKeyAsync method.
func (sps *SignalProtocolService) RotateSignedPreKey(ctx context.Context) (bool, error) {
	sps.mu.Lock()
	defer sps.mu.Unlock()

	shouldRotate := len(sps.preKeys.signedPreKeyHistory) == 0
	if !shouldRotate {
		active := sps.preKeys.signedPreKeyHistory[len(sps.preKeys.signedPreKeyHistory)-1]
		if sps.nowProvider().Sub(active.generatedAt) >= sps.rotationOptions.RotationInterval {
			shouldRotate = true
		}
	}
	if !shouldRotate {
		return false, nil
	}
	if err := sps.appendNewSignedPreKeyLocked(); err != nil {
		return false, err
	}
	sps.tryPersistSignedPreKeysLocked()
	return true, nil
}

// ActiveSignedPreKeyID returns the id of the currently active signed
// pre-key (the newest entry in the history), or 0 if no SPK has been
// generated yet. Mirrors the C# ActiveSignedPreKeyId property.
func (sps *SignalProtocolService) ActiveSignedPreKeyID() int32 {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	if len(sps.preKeys.signedPreKeyHistory) == 0 {
		return 0
	}
	return sps.preKeys.signedPreKeyHistory[len(sps.preKeys.signedPreKeyHistory)-1].id
}

// SignedPreKeyHistoryCount returns the number of signed pre-keys held
// (active + retained-prior). Mirrors the C# SignedPreKeyHistoryCount property.
func (sps *SignalProtocolService) SignedPreKeyHistoryCount() int {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	return len(sps.preKeys.signedPreKeyHistory)
}

// topUpOpkPoolLocked refills the OPK pool to opkPoolSize available
// (un-issued) keys. Generates a fresh X25519 keypair per missing slot,
// allocates a non-colliding id, and enqueues the id at the tail of the
// FIFO available queue.
//
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) topUpOpkPoolLocked() error {
	for len(sps.preKeys.availableOpkIds) < sps.opkPoolSize {
		priv, pub, err := generateX25519KeyPair()
		if err != nil {
			return fmt.Errorf("generate OPK keypair: %w", err)
		}

		// Choose a non-colliding id. Random int32 collisions in a
		// 100-element pool are statistically negligible; guard explicitly
		// to surface a degenerate RNG failure.
		var id int32
		for attempt := 0; ; attempt++ {
			id, err = randomInt32()
			if err != nil {
				ZeroMemory(priv)
				return fmt.Errorf("allocate OPK id: %w", err)
			}
			if _, exists := sps.preKeys.oneTimePreKeys[id]; !exists {
				break
			}
			if attempt+1 >= maxOpkIdAllocAttempts {
				ZeroMemory(priv)
				return fmt.Errorf(
					"could not allocate non-colliding OPK id after %d attempts (pool exhaustion or RNG failure)",
					maxOpkIdAllocAttempts)
			}
		}

		sps.preKeys.oneTimePreKeys[id] = oneTimePreKey{priv: priv, pub: pub}
		sps.preKeys.availableOpkIds = append(sps.preKeys.availableOpkIds, id)
	}
	return nil
}

// OpkPoolSize returns the configured target size of the one-time pre-key
// pool. Useful for tests and observability.
func (sps *SignalProtocolService) OpkPoolSize() int {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	return sps.opkPoolSize
}

// GetOpkPoolStatus returns the current pool counts for tests and
// observability.
//
//   - held is the total number of OPKs we still own — both un-issued
//     (queued) and issued-but-not-yet-consumed (handed out in a bundle
//     but the matching PreKey message hasn't arrived yet).
//   - available is the number of un-issued OPKs in the FIFO queue, ready
//     to be handed out by the next GeneratePreKeyBundle call.
//
// Mirrors the C# HeldOneTimePreKeyCount + AvailableOneTimePreKeyCount pair.
func (sps *SignalProtocolService) GetOpkPoolStatus() (held, available int) {
	sps.mu.Lock()
	defer sps.mu.Unlock()
	return len(sps.preKeys.oneTimePreKeys), len(sps.preKeys.availableOpkIds)
}

// ProcessPreKeyBundle establishes an initiator-side session against the
// supplied bundle via X3DH (Signal §3.3). The initiator's X3DH ephemeral
// is adopted as the first DH-ratchet keypair (Signal-canonical
// integration). The peer's signed pre-key becomes the initial DHr. The
// sending chain key (CKs) is computed lazily on the first Encrypt call.
// The first Encrypt after this returns a PreKey message carrying the
// inputs the responder needs.
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
	// Adopted as the initiator's first DH-ratchet keypair (DHs).
	ekPriv, ekPub, err := generateX25519KeyPair()
	if err != nil {
		return fmt.Errorf("generate ephemeral key: %w", err)
	}

	// X3DH 4-DH key agreement (initiator side).
	dh1, err := x25519Agree(sps.identityX25519Priv, bundle.SignedPreKey)
	if err != nil {
		ZeroMemory(ekPriv)
		return fmt.Errorf("DH1: %w", err)
	}
	defer ZeroMemory(dh1)
	dh2, err := x25519Agree(ekPriv, bundle.IdentityKeyX25519)
	if err != nil {
		ZeroMemory(ekPriv)
		return fmt.Errorf("DH2: %w", err)
	}
	defer ZeroMemory(dh2)
	dh3, err := x25519Agree(ekPriv, bundle.SignedPreKey)
	if err != nil {
		ZeroMemory(ekPriv)
		return fmt.Errorf("DH3: %w", err)
	}
	defer ZeroMemory(dh3)
	dh4, err := x25519Agree(ekPriv, bundle.PreKey)
	if err != nil {
		ZeroMemory(ekPriv)
		return fmt.Errorf("DH4: %w", err)
	}
	defer ZeroMemory(dh4)

	sharedSecret := concat(dh1, dh2, dh3, dh4)
	defer ZeroMemory(sharedSecret)

	rootKey := deriveKey(sharedSecret, hkdfRootInfo)

	// Signal-canonical X3DH↔Double-Ratchet integration: ekPriv becomes the
	// initiator's first DHs; bundle.SignedPreKey is the initial DHr. CKs is
	// computed lazily on first send (DhRatchetSendOnly).
	session := &SignalSession{
		RootKey:                    rootKey,
		SendChainKey:               nil, // computed on first send
		RecvChainKey:               nil, // computed on first DH-ratchet receive
		MyEphemeralPriv:            ekPriv,
		MyEphemeralPub:             ekPub,
		RemoteEphemeralPub:         append([]byte{}, bundle.SignedPreKey...),
		SkippedMessageKeys:         make(map[string][]byte),
		PendingPreKeyMessage:       true,
		InitiatorIdentityKeyX25519: append([]byte{}, sps.identityX25519Pub...),
		UsedSignedPreKeyID:         bundle.SignedPreKeyID,
		UsedOneTimePreKeyID:        bundle.PreKeyID,
	}

	sps.mu.Lock()
	sps.sessions[bundle.Uhid] = session
	sps.tryPersistSessionLocked(bundle.Uhid, session)
	sps.mu.Unlock()

	return nil
}

// establishResponderSessionLocked mirrors the initiator's 4 X3DH DHs to
// derive the same root key, then adopts the SPK keypair as the responder's
// initial DHs. RemoteEphemeralPub is left nil so the immediately-following
// Decrypt call triggers a DH-ratchet step (which rotates DHs to a fresh
// keypair). Consumes (and zeros) the one-time pre-key.
//
// SPK lookup walks the full retained history (active + retained-prior) so
// messages signed under a recently-rotated SPK still complete X3DH during
// the rotation window. A pruned SPK fails outright because its private
// half has been zeroed and removed from the history.
//
// Caller MUST hold sps.mu.
func (sps *SignalProtocolService) establishResponderSessionLocked(peerUhid string, payload *EncryptedPayload, initiatorRatchetPub []byte) error {
	if len(payload.InitiatorIdentityKeyX25519) != X25519PublicKeySize {
		return fmt.Errorf("initiator IK_X25519 has wrong size: %d (want %d)", len(payload.InitiatorIdentityKeyX25519), X25519PublicKeySize)
	}
	if len(initiatorRatchetPub) != X25519PublicKeySize {
		return fmt.Errorf("initiator ratchet pub has wrong size: %d (want %d)", len(initiatorRatchetPub), X25519PublicKeySize)
	}
	spkEntry := sps.findSignedPreKeyLocked(payload.UsedSignedPreKeyID)
	if spkEntry == nil || len(spkEntry.priv) == 0 {
		return fmt.Errorf("PreKey message references signed pre-key id %d which is not held by this node (rotated out or never generated)", payload.UsedSignedPreKeyID)
	}
	otpk, ok := sps.preKeys.oneTimePreKeys[payload.UsedOneTimePreKeyID]
	if !ok {
		return fmt.Errorf("PreKey message references one-time pre-key id %d which is not held (already consumed?)", payload.UsedOneTimePreKeyID)
	}

	// Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
	dh1, err := x25519Agree(spkEntry.priv, payload.InitiatorIdentityKeyX25519)
	if err != nil {
		return fmt.Errorf("DH1': %w", err)
	}
	defer ZeroMemory(dh1)
	dh2, err := x25519Agree(sps.identityX25519Priv, initiatorRatchetPub)
	if err != nil {
		return fmt.Errorf("DH2': %w", err)
	}
	defer ZeroMemory(dh2)
	dh3, err := x25519Agree(spkEntry.priv, initiatorRatchetPub)
	if err != nil {
		return fmt.Errorf("DH3': %w", err)
	}
	defer ZeroMemory(dh3)
	dh4, err := x25519Agree(otpk.priv, initiatorRatchetPub)
	if err != nil {
		return fmt.Errorf("DH4': %w", err)
	}
	defer ZeroMemory(dh4)

	sharedSecret := concat(dh1, dh2, dh3, dh4)
	defer ZeroMemory(sharedSecret)

	rootKey := deriveKey(sharedSecret, hkdfRootInfo)

	// Adopt SPK as the initial DHs. RemoteEphemeralPub is intentionally nil
	// so the DH-ratchet step at the start of the very next Decrypt step
	// rotates DHs to a fresh keypair and derives both the receive chain
	// (from old DHs · new DHr) and the new sending chain (from new DHs ·
	// new DHr).
	session := &SignalSession{
		RootKey:            rootKey,
		SendChainKey:       nil,
		RecvChainKey:       nil,
		MyEphemeralPriv:    append([]byte{}, spkEntry.priv...),
		MyEphemeralPub:     append([]byte{}, spkEntry.pub...),
		RemoteEphemeralPub: nil, // forces DH-ratchet on the first decrypt below
		SkippedMessageKeys: make(map[string][]byte),
	}
	sps.sessions[peerUhid] = session

	// Consume one-time pre-key (zero + remove). Replay protection at the
	// bundle layer.
	ZeroMemory(otpk.priv)
	delete(sps.preKeys.oneTimePreKeys, payload.UsedOneTimePreKeyID)

	// Persist: OPK pool changed (one consumed) and a new session was created.
	sps.tryConsumeOneTimePreKeyLocked(payload.UsedOneTimePreKeyID)
	sps.tryPersistOneTimePreKeysLocked()
	sps.tryPersistSessionLocked(peerUhid, session)

	return nil
}

// dhRatchetReceive performs a full DH-ratchet step on receive (Signal §5.2):
// updates DHr, derives a new receiving chain via KDF_RK(RK, DH(DHs, DHr)),
// generates a fresh DHs, and derives a new sending chain via
// KDF_RK(RK, DH(newDHs, DHr)).
func dhRatchetReceive(session *SignalSession, newRemoteEphemeralPub []byte) error {
	// Save send-counter as PN so the peer can compute skipped keys across
	// the ratchet boundary on subsequent decrypts.
	session.PreviousChainCount = session.SendCounter
	session.SendCounter = 0
	session.RecvCounter = 0
	session.RemoteEphemeralPub = append([]byte{}, newRemoteEphemeralPub...)

	// Step 1: derive new receiving chain from current DHs · new DHr.
	dh1, err := x25519Agree(session.MyEphemeralPriv, session.RemoteEphemeralPub)
	if err != nil {
		return fmt.Errorf("dh-ratchet recv DH (current DHs · new DHr): %w", err)
	}
	defer ZeroMemory(dh1)
	newRoot, newCkr, err := kdfRk(session.RootKey, dh1)
	if err != nil {
		return fmt.Errorf("KDF_RK recv: %w", err)
	}
	session.RootKey = newRoot
	session.RecvChainKey = newCkr

	// Step 2: rotate DHs to a fresh keypair, derive new sending chain
	// from new DHs · new DHr.
	ZeroMemory(session.MyEphemeralPriv)
	newPriv, newPub, err := generateX25519KeyPair()
	if err != nil {
		return fmt.Errorf("generate new DHs: %w", err)
	}
	session.MyEphemeralPriv = newPriv
	session.MyEphemeralPub = newPub

	dh2, err := x25519Agree(session.MyEphemeralPriv, session.RemoteEphemeralPub)
	if err != nil {
		return fmt.Errorf("dh-ratchet send DH (new DHs · new DHr): %w", err)
	}
	defer ZeroMemory(dh2)
	newRoot2, newCks, err := kdfRk(session.RootKey, dh2)
	if err != nil {
		return fmt.Errorf("KDF_RK send: %w", err)
	}
	session.RootKey = newRoot2
	session.SendChainKey = newCks

	return nil
}

// dhRatchetSendOnly is the lazy half-ratchet for the very first send on a
// freshly-established initiator session. The initiator's DHs and DHr are
// already set (X3DH placed them); we just need to derive the sending chain.
// We do NOT rotate DHs here — only on a true DH-ratchet (i.e. on receive).
func dhRatchetSendOnly(session *SignalSession, remotePub []byte) error {
	dh, err := x25519Agree(session.MyEphemeralPriv, remotePub)
	if err != nil {
		return fmt.Errorf("send-only DH: %w", err)
	}
	defer ZeroMemory(dh)
	newRoot, newCks, err := kdfRk(session.RootKey, dh)
	if err != nil {
		return fmt.Errorf("KDF_RK send-only: %w", err)
	}
	session.RootKey = newRoot
	session.SendChainKey = newCks
	return nil
}

// skipMessageKeys saves any unread message keys on the current receive
// chain up to the given counter, so they can be consumed if those messages
// eventually arrive after a DH-ratchet step. Bounded by MaxSkippedKeys.
func skipMessageKeys(session *SignalSession, until int32) error {
	if session.RecvChainKey == nil || session.RemoteEphemeralPub == nil {
		return nil // no chain to skip on
	}
	if until <= session.RecvCounter {
		return nil
	}
	if until-session.RecvCounter > MaxSkippedKeys {
		return fmt.Errorf("skipped-key request exceeds maximum (%d), session must be re-established", MaxSkippedKeys)
	}

	for session.RecvCounter < until {
		newChain, skipKey, err := ratchetChainKey(session.RecvChainKey)
		if err != nil {
			return fmt.Errorf("ratchet skipped chain key: %w", err)
		}
		session.RecvChainKey = newChain
		session.SkippedMessageKeys[skippedKey(session.RemoteEphemeralPub, session.RecvCounter)] = skipKey
		session.RecvCounter++
	}
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

// deriveKey derives a 32-byte key from input key material using HKDF-SHA256
// with no salt. Used for X3DH initial root-key derivation and the legacy
// chain-key labels (still pinned by the cross-language fixtures).
func deriveKey(ikm []byte, info []byte) []byte {
	h := hkdf.New(sha256.New, ikm, nil, info)
	key := make([]byte, aesKeySize)
	if _, err := h.Read(key); err != nil {
		panic(err) // HKDF over fixed inputs cannot fail.
	}
	return key
}

// kdfRk implements KDF_RK per Signal §5.2: derives a new root key + new
// chain key from the current root key (used as the HKDF salt) and a fresh
// DH output (used as IKM). HKDF-SHA256, 64-byte output split into
// (newRootKey[0..32], newChainKey[32..64]).
func kdfRk(rootKey, dhOutput []byte) (newRoot, newChain []byte, err error) {
	h := hkdf.New(sha256.New, dhOutput, rootKey, hkdfRatchetInfo)
	derived := make([]byte, 64)
	if _, err := h.Read(derived); err != nil {
		return nil, nil, fmt.Errorf("HKDF read: %w", err)
	}
	defer ZeroMemory(derived)
	newRoot = make([]byte, 32)
	newChain = make([]byte, 32)
	copy(newRoot, derived[0:32])
	copy(newChain, derived[32:64])
	return newRoot, newChain, nil
}

// ratchetChainKey advances a chain key by one step per Signal §5.1.
//
//	message_key   = HMAC-SHA256(chain_key, 0x01)
//	new_chain_key = HMAC-SHA256(chain_key, 0x02)
func ratchetChainKey(chainKey []byte) (newChain, messageKey []byte, err error) {
	h1 := hmac.New(sha256.New, chainKey)
	h1.Write([]byte{0x01})
	messageKey = h1.Sum(nil)

	h2 := hmac.New(sha256.New, chainKey)
	h2.Write([]byte{0x02})
	newChain = h2.Sum(nil)
	return newChain, messageKey, nil
}

// skippedKey returns the cache key for a skipped message: "Hex(DHr_pub):counter".
// Binding to the remote DHr public key is essential — out-of-order messages
// from a previous chain (different DHr) can still arrive after a DH-ratchet
// step, and they need their own per-chain key set.
func skippedKey(dhrPub []byte, counter int32) string {
	return fmt.Sprintf("%s:%d", hex.EncodeToString(dhrPub), counter)
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

// constantTimeEqual reports whether a and b have the same length and bytes,
// using constant-time comparison.
func constantTimeEqual(a, b []byte) bool {
	if len(a) != len(b) {
		return false
	}
	return subtle.ConstantTimeCompare(a, b) == 1
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

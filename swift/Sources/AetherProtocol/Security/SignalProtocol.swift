// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// State of a Signal-Protocol session with a single peer — both X3DH
/// session-establishment metadata and Double-Ratchet (Signal §5) state.
///
/// Double-Ratchet state per Signal §5:
///   - RK — root key. Re-keyed on every DH-ratchet step.
///   - DHs (priv/pub) — my current ratchet keypair.
///   - DHr — peer's last-known ratchet public key. Nil until first DH-ratchet.
///   - CKs — my current sending chain key. Nil until I've sent (or initialized) on this chain.
///   - CKr — my current receiving chain key. Nil until I've received on this chain.
///   - Ns / Nr — send / receive counters (reset to 0 on each DH-ratchet step).
///   - PN — number of messages I sent in my previous sending chain (so the
///     receiver can compute skipped keys across a DH-ratchet boundary).
///   - MKSKIPPED — skipped message keys keyed by (DHr_pub, counter).
internal class SignalSession {
    var rootKey: Data

    /// Sending chain key. Nil until the first send (or until DH-ratchet rekeys it).
    var sendChainKey: Data?
    /// Receiving chain key. Nil until the first receive that triggers a DH-ratchet step.
    var recvChainKey: Data?

    var sendCounter: Int32 = 0
    var recvCounter: Int32 = 0
    /// Number of messages sent in the previous sending chain (Signal §5: PN).
    var previousChainCount: Int32 = 0

    /// My current DH-ratchet private key (X25519, 32 bytes raw).
    var myEphemeralPriv: Data = Data()
    /// My current DH-ratchet public key (X25519, 32 bytes).
    var myEphemeralPub: Data = Data()
    /// Peer's last-seen DH-ratchet public key. Nil until first DH-ratchet step.
    var remoteEphemeralPub: Data?

    /// Skipped message keys keyed by "hex(remoteEphPub):counter". The
    /// remoteEphPub binding is essential — out-of-order messages from a
    /// previous chain (different DHr) can still arrive after a DH-ratchet
    /// step, and they need their own per-chain key set.
    var skippedMessageKeys: [String: Data] = [:]

    /// True iff this session was established in the initiator role and the
    /// first outbound message has not yet been sent. While true, the next
    /// `encrypt(...)` emits a PreKey message (messageType = 1) carrying the
    /// X3DH inputs.
    var pendingPreKeyMessage: Bool = false
    var initiatorIdentityKeyX25519: Data = Data()
    var usedSignedPreKeyId: Int32 = 0
    var usedOneTimePreKeyId: Int32 = 0

    init(rootKey: Data) {
        self.rootKey = rootKey
    }
}

/// Responder-side pre-key state. Holds the private halves of the signed
/// pre-key and one-time pre-keys so X3DH can be computed when an
/// initiator's PreKey message arrives.
///
/// One-time pre-keys are managed as a pool of `opkPoolSize` entries
/// (default 100, mirrors the Signal-published guidance). Bundle generation
/// hands out the next-unused id from `availableOpkIds`; the OPK stays in
/// `oneTimePreKeys` until a responder consumes it via X3DH, at which point
/// it is removed. Top-up runs each time a bundle is generated so the
/// available queue never empties under steady load.
internal struct PreKeyState {
    var signedPreKeyId: Int32 = 0
    var signedPreKeyPriv: Data = Data()
    var signedPreKeyPub: Data = Data()
    var signedPreKeySignature: Data = Data()

    /// id -> (priv, pub). Each entry is consumed (zeroed and removed) on first use.
    var oneTimePreKeys: [Int32: (priv: Data, pub: Data)] = [:]

    /// IDs of OPKs that exist in `oneTimePreKeys` and have NOT yet been
    /// issued in any bundle. Bundle generation pops from the front (FIFO);
    /// top-up appends to the end. Modelled as a `[Int32]` because Swift has
    /// no built-in queue type — `removeFirst()` + `append(_:)` give us the
    /// same FIFO semantics as the C# `Queue<int>`.
    var availableOpkIds: [Int32] = []
}

/// Signal Protocol implementation: X3DH session establishment + full
/// Double Ratchet (Signal §5).
///
/// Key agreement: X3DH (Signal §3) over X25519 (RFC 7748). Four DHs:
///   - DH1 = DH(IK_A, SPK_B) — long-term mutual auth
///   - DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
///   - DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
///   - DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
/// Initial root key: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
///
/// Double Ratchet (§5): each side maintains a current X25519 ratchet
/// keypair. Whenever the sender receives a peer message bearing a new
/// ratchet public key, it does a DH-ratchet step: derive a new chain key
/// via `KDF_RK(RK, DH(myDHs_priv, newDHr))`, then generate a fresh DHs
/// and derive its sending chain via `KDF_RK(RK, DH(newDHs_priv, newDHr))`.
/// The Signal-canonical integration with X3DH is used: the initiator's
/// X3DH ephemeral key becomes its first DH-ratchet keypair.
///
/// Symmetric ratchet (§5.1): HMAC-SHA256, single-byte domain separation
/// (0x01 -> message key, 0x02 -> next chain key).
/// Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
/// Identity signing: Ed25519 via Ed25519Service.
public actor SignalProtocolService {
    public static let maxSkippedKeys: Int = 1000

    public static let messageTypeNormal: Int32 = 0
    public static let messageTypePreKey: Int32 = 1

    /// Default size of the one-time pre-key pool. Mirrors the Signal-
    /// published guidance: ~100 OPKs per device so realistic concurrent-
    /// initiator loads don't collide on a single shared id. Matches
    /// `SignalProtocolService.DefaultOpkPoolSize` in the C# port.
    public static let defaultOpkPoolSize: Int = 100

    private let aesNonceSize: Int = 12
    private let aesTagSize: Int = 16
    private let aesKeySize: Int = 32

    /// Target size of the one-time pre-key pool. The pool is topped up to
    /// this many available (un-issued) keys on every bundle generation, and
    /// consumed keys are replaced lazily on the next bundle call.
    public let opkPoolSize: Int

    /// HKDF info string for the X3DH root-key derivation. MUST match every
    /// other language exactly — verified by fixtures/signal/expected/x3dh_basic.json.
    private let hkdfRootInfo = "aether-x3dh-root-v1".data(using: .utf8)!

    /// HKDF info string for the DH-ratchet step (Signal §5: KDF_RK). Each
    /// DH-ratchet step derives a 64-byte block, split into the new root key
    /// (first 32 bytes) and the new chain key (second 32 bytes).
    private let hkdfRatchetInfo = "aether-ratchet-rk-v1".data(using: .utf8)!

    private var sessions: [String: SignalSession] = [:]

    // Long-term identity keys — two distinct keypairs per node.
    private var identityX25519Priv: Curve25519.KeyAgreement.PrivateKey
    private var identityX25519Pub: Data
    private var ed25519PrivateKey: Data
    private var ed25519PublicKey: Data

    private var localUhid: String?
    private var preKeys: PreKeyState = PreKeyState()

    public init(opkPoolSize: Int = SignalProtocolService.defaultOpkPoolSize) {
        precondition(opkPoolSize >= 1, "opkPoolSize must be >= 1 (got \(opkPoolSize)).")
        self.opkPoolSize = opkPoolSize

        (self.ed25519PrivateKey, self.ed25519PublicKey) = Ed25519Service.generateKeyPair()

        // X25519 long-term identity for X3DH ECDH.
        let priv = Curve25519.KeyAgreement.PrivateKey()
        self.identityX25519Priv = priv
        self.identityX25519Pub = priv.publicKey.rawRepresentation
    }

    /// Sets the local node's UHID. Required before any encrypt() call.
    public func setLocalUhid(_ uhid: String) {
        self.localUhid = uhid
    }

    public func hasSession(peerUhid: String) -> Bool {
        sessions[peerUhid] != nil
    }

    /// Encrypts plaintext for a peer. The first message after initiator-side
    /// X3DH is returned with messageType = PreKey and carries the X3DH inputs
    /// the responder needs to derive the same root key.
    public func encrypt(peerUhid: String, plaintext: Data) throws -> EncryptedPayload {
        guard let session = sessions[peerUhid] else {
            throw SignalProtocolError.noSessionEstablished(peerUhid)
        }
        guard let senderUhid = localUhid else {
            throw SignalProtocolError.localUhidNotSet
        }

        // Lazy CKs initialization for the initiator's first send: the X3DH
        // setup placed DHs and DHr but did not derive CKs (the Double
        // Ratchet defers it until first send to avoid an extra KDF step
        // when no message is ever sent on a session).
        if session.sendChainKey == nil {
            guard let remotePub = session.remoteEphemeralPub else {
                throw SignalProtocolError.noSessionEstablished(
                    "Cannot derive sending chain: peer's ratchet public key is unknown."
                )
            }
            try dhRatchetSendOnly(session: session, remotePub: remotePub)
        }

        guard let currentSendChain = session.sendChainKey else {
            throw SignalProtocolError.failedToObtainMessageKey
        }
        let (newChain, messageKey) = ratchetChainKey(currentSendChain)
        session.sendChainKey = newChain

        var nonce = Data(count: aesNonceSize)
        _ = nonce.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, aesNonceSize, buffer.baseAddress!)
        }

        let sealedBox = try AES.GCM.seal(
            plaintext,
            using: SymmetricKey(data: messageKey),
            nonce: try AES.GCM.Nonce(data: nonce)
        )
        var combined = Data()
        combined.append(sealedBox.ciphertext)
        combined.append(sealedBox.tag)

        let counter = session.sendCounter
        session.sendCounter += 1

        let ratchetPub = session.myEphemeralPub

        // PreKey message? First message after initiator-side X3DH. Carries
        // X3DH metadata so the responder can mirror the DHs. The
        // initiatorEphemeralKeyX25519 alias equals senderEphemeralKeyX25519
        // because the initiator's X3DH ephemeral becomes its first
        // DH-ratchet pubkey.
        if session.pendingPreKeyMessage {
            let payload = EncryptedPayload(
                ciphertext: combined,
                nonce: nonce,
                messageType: Self.messageTypePreKey,
                senderUhid: senderUhid,
                counter: counter,
                initiatorIdentityKeyX25519: session.initiatorIdentityKeyX25519,
                initiatorEphemeralKeyX25519: ratchetPub,
                usedSignedPreKeyId: session.usedSignedPreKeyId,
                usedOneTimePreKeyId: session.usedOneTimePreKeyId,
                senderEphemeralKeyX25519: ratchetPub,
                previousChainCount: session.previousChainCount
            )
            session.pendingPreKeyMessage = false
            zero(messageKey)
            return payload
        }

        zero(messageKey)
        return EncryptedPayload(
            ciphertext: combined,
            nonce: nonce,
            messageType: Self.messageTypeNormal,
            senderUhid: senderUhid,
            counter: counter,
            senderEphemeralKeyX25519: ratchetPub,
            previousChainCount: session.previousChainCount
        )
    }

    /// Decrypts a payload. If messageType = PreKey, establishes (or replaces)
    /// the responder-side session via mirrored X3DH first.
    public func decrypt(peerUhid: String, payload: EncryptedPayload) throws -> Data {
        // Every Double-Ratchet message carries the sender's current ratchet
        // public key. Fall back to initiatorEphemeralKeyX25519 for backward
        // compatibility with older PreKey messages from peers that haven't
        // upgraded to the new wire envelope.
        let senderRatchetPub = payload.senderEphemeralKeyX25519
            ?? payload.initiatorEphemeralKeyX25519

        // PreKey message? Establish the responder-side session via mirrored X3DH.
        if payload.messageType == Self.messageTypePreKey {
            guard let initiatorIK = payload.initiatorIdentityKeyX25519,
                  let initiatorRatchet = senderRatchetPub
            else {
                throw SignalProtocolError.preKeyMessageMissingMaterial
            }
            try establishResponderSession(
                peerUhid: peerUhid,
                initiatorIK: initiatorIK,
                initiatorRatchetPub: initiatorRatchet,
                usedSignedPreKeyId: payload.usedSignedPreKeyId,
                usedOneTimePreKeyId: payload.usedOneTimePreKeyId
            )
        }

        guard let session = sessions[peerUhid] else {
            throw SignalProtocolError.noSessionEstablished(peerUhid)
        }

        guard let senderRatchet = senderRatchetPub else {
            throw SignalProtocolError.preKeyMessageMissingMaterial
        }

        // DH-ratchet step? Triggered when the peer's ratchet public key changes.
        if session.remoteEphemeralPub == nil
            || !constantTimeEquals(senderRatchet, session.remoteEphemeralPub!)
        {
            // First, derive any skipped keys from the previous receive chain
            // (the chain keyed by the OLD remoteEphemeralPub). Then ratchet.
            try skipMessageKeys(session: session, until: payload.previousChainCount)
            try dhRatchetReceive(session: session, newRemoteEphemeralPub: senderRatchet)
        }

        guard payload.ciphertext.count >= aesTagSize else {
            throw SignalProtocolError.ciphertextTooShort
        }

        var messageKey: Data
        let skippedLookupKey = skippedKeyId(remoteEphPub: senderRatchet, counter: payload.counter)
        if let cached = session.skippedMessageKeys.removeValue(forKey: skippedLookupKey) {
            messageKey = cached
        } else {
            guard let currentRecvChain = session.recvChainKey else {
                throw SignalProtocolError.failedToObtainMessageKey
            }

            let gap = payload.counter - session.recvCounter
            if gap > Int32(Self.maxSkippedKeys) {
                throw SignalProtocolError.excessiveCounterGap(Int(gap), Self.maxSkippedKeys)
            }

            // Skip ahead, caching intermediate keys.
            var workingChain = currentRecvChain
            while session.recvCounter < payload.counter {
                let (nextChain, skipKey) = ratchetChainKey(workingChain)
                workingChain = nextChain
                let skipLookup = skippedKeyId(
                    remoteEphPub: senderRatchet,
                    counter: session.recvCounter
                )
                session.skippedMessageKeys[skipLookup] = skipKey
                session.recvCounter += 1
            }

            let (newChain, key) = ratchetChainKey(workingChain)
            session.recvChainKey = newChain
            messageKey = key
            session.recvCounter += 1
        }

        let ciphertextLength = payload.ciphertext.count - aesTagSize
        let ciphertext = payload.ciphertext.subdata(in: 0 ..< ciphertextLength)
        let tag = payload.ciphertext.subdata(in: ciphertextLength ..< payload.ciphertext.count)

        let sealedBox = try AES.GCM.SealedBox(
            nonce: try AES.GCM.Nonce(data: payload.nonce),
            ciphertext: ciphertext,
            tag: tag
        )
        let plaintext = try AES.GCM.open(sealedBox, using: SymmetricKey(data: messageKey))
        zero(messageKey)
        return plaintext
    }

    /// Generates a pre-key bundle. Retains the SPK + OPK private halves for
    /// responder-side X3DH on this node.
    ///
    /// One-time pre-keys are managed as a pool of `opkPoolSize` (default
    /// 100) un-issued keys; this method tops the pool back up to that
    /// target on every call, then dequeues the next un-issued OPK from the
    /// front of `availableOpkIds`. Actor isolation is sufficient — there's
    /// no race between concurrent bundle generations on the same actor.
    public func generatePreKeyBundle(localUhid: String) throws -> PreKeyBundle {
        self.localUhid = localUhid

        // Signed pre-key — generate lazily on the first bundle call. The
        // active SPK is reused on subsequent calls (the C# port supports
        // periodic SPK rotation; mirror just the basic single-active-SPK
        // behaviour here, which all existing Swift fixtures expect).
        if preKeys.signedPreKeyPriv.isEmpty {
            let spkPriv = Curve25519.KeyAgreement.PrivateKey()
            let spkPub = spkPriv.publicKey.rawRepresentation
            let signedPreKeyId = randomPositiveInt32()
            let signature = try Ed25519Service.sign(ed25519PrivateKey, spkPub)
            preKeys.signedPreKeyId = signedPreKeyId
            preKeys.signedPreKeyPriv = spkPriv.rawRepresentation
            preKeys.signedPreKeyPub = spkPub
            preKeys.signedPreKeySignature = signature
        }

        // Top up the OPK pool, then dequeue the next un-issued OPK.
        topUpOpkPool()
        guard !preKeys.availableOpkIds.isEmpty else {
            // Should be unreachable after topUpOpkPool() succeeds, but guard
            // explicitly so a future refactor can't silently regress to a
            // single-OPK reuse.
            throw SignalProtocolError.failedToObtainMessageKey
        }
        let preKeyId = preKeys.availableOpkIds.removeFirst()
        guard let entry = preKeys.oneTimePreKeys[preKeyId] else {
            throw SignalProtocolError.preKeyNotHeld(.oneTimePreKey, preKeyId)
        }
        let otpkPub = entry.pub

        return PreKeyBundle(
            uhid: localUhid,
            identityKey: ed25519PublicKey,
            identityKeyX25519: identityX25519Pub,
            preKeyId: preKeyId,
            preKey: otpkPub,
            signedPreKeyId: preKeys.signedPreKeyId,
            signedPreKey: preKeys.signedPreKeyPub,
            signedPreKeySignature: preKeys.signedPreKeySignature
        )
    }

    /// Tops the OPK pool up to `opkPoolSize` available (un-issued) keys.
    /// Generates a fresh X25519 keypair per missing slot, assigns it a
    /// random non-colliding id, and enqueues the id at the end of
    /// `availableOpkIds`. Idempotent — safe to call repeatedly.
    private func topUpOpkPool() {
        while preKeys.availableOpkIds.count < opkPoolSize {
            let priv = Curve25519.KeyAgreement.PrivateKey()
            let pub = priv.publicKey.rawRepresentation

            // Choose a non-colliding id. The 2^31 range makes collisions in
            // a 100-element pool statistically negligible, but guard
            // explicitly to match the C# implementation.
            var id: Int32 = 0
            var attempts = 0
            repeat {
                id = randomPositiveInt32()
                attempts += 1
                if attempts > 64 {
                    // Mirrors the C# CryptographicException("Could not allocate
                    // a non-colliding OPK id after 64 attempts.").
                    fatalError("Could not allocate a non-colliding OPK id after 64 attempts. " +
                               "Pool exhaustion or RNG failure.")
                }
            } while preKeys.oneTimePreKeys[id] != nil

            preKeys.oneTimePreKeys[id] = (priv: priv.rawRepresentation, pub: pub)
            preKeys.availableOpkIds.append(id)
        }
    }

    /// Pool observability: total OPKs held (un-issued + issued-but-not-yet-
    /// consumed) and the count still un-issued. Mirrors the C# props
    /// `HeldOneTimePreKeyCount` and `AvailableOneTimePreKeyCount`.
    public func getOpkPoolStatus() -> (held: Int, available: Int) {
        return (held: preKeys.oneTimePreKeys.count, available: preKeys.availableOpkIds.count)
    }

    /// Establishes initiator-side session via X3DH (Signal §3.3): generates
    /// a fresh ephemeral X25519 keypair, runs the four DHs, derives the root
    /// key, and primes the Double Ratchet by adopting the X3DH ephemeral as
    /// the initiator's first DHs. The peer's signed pre-key becomes the
    /// initial DHr. The first `encrypt(...)` after this returns a PreKey
    /// message (messageType = 1).
    public func processPreKeyBundle(_ bundle: PreKeyBundle) throws {
        guard Ed25519Service.verify(bundle.identityKey, bundle.signedPreKey, bundle.signedPreKeySignature) else {
            throw SignalProtocolError.signatureVerificationFailed
        }
        guard bundle.identityKeyX25519.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("identityKeyX25519 length \(bundle.identityKeyX25519.count) != 32")
        }
        guard bundle.signedPreKey.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("signedPreKey length \(bundle.signedPreKey.count) != 32")
        }
        guard bundle.preKey.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("preKey length \(bundle.preKey.count) != 32")
        }

        // Fresh ephemeral X25519 keypair, generated per-session.
        let ekPriv = Curve25519.KeyAgreement.PrivateKey()
        let ekPub = ekPriv.publicKey.rawRepresentation

        // X3DH 4-DH key agreement (initiator side).
        let dh1 = try x25519Agree(privateKey: identityX25519Priv, publicKey: bundle.signedPreKey)
        let dh2 = try x25519Agree(privateKey: ekPriv, publicKey: bundle.identityKeyX25519)
        let dh3 = try x25519Agree(privateKey: ekPriv, publicKey: bundle.signedPreKey)
        let dh4 = try x25519Agree(privateKey: ekPriv, publicKey: bundle.preKey)

        var shared = Data()
        shared.append(dh1)
        shared.append(dh2)
        shared.append(dh3)
        shared.append(dh4)

        let rootKey = hkdf32(shared, info: hkdfRootInfo)

        // Signal-canonical X3DH<->Double-Ratchet integration: the initiator's
        // X3DH ephemeral becomes its first DHs. The peer's signed pre-key is
        // the initial DHr. CKs is computed lazily on first send.
        let session = SignalSession(rootKey: rootKey)
        session.sendChainKey = nil          // computed on first send
        session.recvChainKey = nil          // computed on first DH-ratchet receive
        session.myEphemeralPriv = ekPriv.rawRepresentation
        session.myEphemeralPub = ekPub
        session.remoteEphemeralPub = bundle.signedPreKey
        session.pendingPreKeyMessage = true
        session.initiatorIdentityKeyX25519 = identityX25519Pub
        session.usedSignedPreKeyId = bundle.signedPreKeyId
        session.usedOneTimePreKeyId = bundle.preKeyId

        sessions[bundle.uhid] = session
        zero(shared)
    }

    /// Mirrors the initiator's 4 X3DH DHs to derive the same root key, then
    /// adopts the SPK (private + public) as the responder's initial DHs.
    /// `remoteEphemeralPub` is left nil so the first decrypt forces a
    /// DH-ratchet step (the message header carries the initiator's first
    /// DHs). The one-time pre-key is consumed.
    private func establishResponderSession(
        peerUhid: String,
        initiatorIK: Data,
        initiatorRatchetPub: Data,
        usedSignedPreKeyId: Int32,
        usedOneTimePreKeyId: Int32
    ) throws {
        guard initiatorIK.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("initiator IK length \(initiatorIK.count) != 32")
        }
        guard initiatorRatchetPub.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("initiator ratchet pub length \(initiatorRatchetPub.count) != 32")
        }
        guard preKeys.signedPreKeyId == usedSignedPreKeyId, !preKeys.signedPreKeyPriv.isEmpty else {
            throw SignalProtocolError.preKeyNotHeld(.signedPreKey, usedSignedPreKeyId)
        }
        guard let otpk = preKeys.oneTimePreKeys[usedOneTimePreKeyId] else {
            throw SignalProtocolError.preKeyNotHeld(.oneTimePreKey, usedOneTimePreKeyId)
        }

        let spkPrivKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: preKeys.signedPreKeyPriv)
        let identityPrivKey = identityX25519Priv
        let otpkPrivKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: otpk.priv)

        // Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
        let dh1 = try x25519Agree(privateKey: spkPrivKey, publicKey: initiatorIK)
        let dh2 = try x25519Agree(privateKey: identityPrivKey, publicKey: initiatorRatchetPub)
        let dh3 = try x25519Agree(privateKey: spkPrivKey, publicKey: initiatorRatchetPub)
        let dh4 = try x25519Agree(privateKey: otpkPrivKey, publicKey: initiatorRatchetPub)

        var shared = Data()
        shared.append(dh1)
        shared.append(dh2)
        shared.append(dh3)
        shared.append(dh4)

        let rootKey = hkdf32(shared, info: hkdfRootInfo)

        // Adopt SPK as the initial DHs. The DH-ratchet step that follows
        // (triggered on the first decrypt below) will rotate it to a fresh
        // keypair.
        let session = SignalSession(rootKey: rootKey)
        session.sendChainKey = nil
        session.recvChainKey = nil
        session.myEphemeralPriv = preKeys.signedPreKeyPriv
        session.myEphemeralPub = preKeys.signedPreKeyPub
        session.remoteEphemeralPub = nil      // forces DH-ratchet on first decrypt
        session.pendingPreKeyMessage = false

        sessions[peerUhid] = session

        // Consume the one-time pre-key — never reuse.
        preKeys.oneTimePreKeys.removeValue(forKey: usedOneTimePreKeyId)
        zero(shared)
    }

    public func getPublicKey() -> Data {
        ed25519PublicKey
    }

    public func getX25519PublicKey() -> Data {
        identityX25519Pub
    }

    // MARK: - Double Ratchet (Signal §5)

    /// Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
    /// derives a new receiving chain via `KDF_RK(RK, DH(DHs, DHr))`,
    /// generates a fresh DHs, and derives a new sending chain via
    /// `KDF_RK(RK, DH(newDHs, DHr))`.
    private func dhRatchetReceive(session: SignalSession, newRemoteEphemeralPub: Data) throws {
        // Save send-counter as PN so the peer can compute skipped keys
        // across the ratchet boundary on subsequent decrypts.
        session.previousChainCount = session.sendCounter
        session.sendCounter = 0
        session.recvCounter = 0
        session.remoteEphemeralPub = newRemoteEphemeralPub

        // Step 1: derive new receiving chain from current DHs · new DHr.
        let myPriv = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: session.myEphemeralPriv)
        let dh1 = try x25519Agree(privateKey: myPriv, publicKey: newRemoteEphemeralPub)
        let (newRoot1, newRecvChain) = kdfRk(rootKey: session.rootKey, dhOutput: dh1)
        session.rootKey = newRoot1
        session.recvChainKey = newRecvChain
        zero(dh1)

        // Step 2: rotate DHs to a fresh keypair, derive new sending chain
        // from new DHs · new DHr.
        zero(session.myEphemeralPriv)
        let newPriv = Curve25519.KeyAgreement.PrivateKey()
        session.myEphemeralPriv = newPriv.rawRepresentation
        session.myEphemeralPub = newPriv.publicKey.rawRepresentation

        let dh2 = try x25519Agree(privateKey: newPriv, publicKey: newRemoteEphemeralPub)
        let (newRoot2, newSendChain) = kdfRk(rootKey: session.rootKey, dhOutput: dh2)
        session.rootKey = newRoot2
        session.sendChainKey = newSendChain
        zero(dh2)
    }

    /// Lazy half-ratchet for the very first send on a freshly-established
    /// initiator session. The initiator's DHs and DHr are already set (X3DH
    /// placed them); we just need to derive the sending chain. We do NOT
    /// rotate DHs here — only on a true DH-ratchet (i.e. on receive).
    private func dhRatchetSendOnly(session: SignalSession, remotePub: Data) throws {
        let myPriv = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: session.myEphemeralPriv)
        let dh = try x25519Agree(privateKey: myPriv, publicKey: remotePub)
        let (newRoot, newSendChain) = kdfRk(rootKey: session.rootKey, dhOutput: dh)
        session.rootKey = newRoot
        session.sendChainKey = newSendChain
        zero(dh)
    }

    /// Saves any unread message keys on the current receive chain up to the
    /// given counter, so they can be consumed if those messages eventually
    /// arrive after a DH-ratchet step. Bounded by `maxSkippedKeys`.
    private func skipMessageKeys(session: SignalSession, until: Int32) throws {
        // No chain to skip on (e.g. responder receiving its first message
        // — recvChainKey is nil, remoteEphemeralPub is nil).
        guard session.recvChainKey != nil,
              let oldRemoteEphPub = session.remoteEphemeralPub else {
            return
        }
        if until <= session.recvCounter {
            return
        }
        if until - session.recvCounter > Int32(Self.maxSkippedKeys) {
            throw SignalProtocolError.excessiveCounterGap(Int(until - session.recvCounter), Self.maxSkippedKeys)
        }

        while session.recvCounter < until {
            guard let currentRecvChain = session.recvChainKey else { return }
            let (nextChain, skipKey) = ratchetChainKey(currentRecvChain)
            session.recvChainKey = nextChain
            let lookup = skippedKeyId(remoteEphPub: oldRemoteEphPub, counter: session.recvCounter)
            session.skippedMessageKeys[lookup] = skipKey
            session.recvCounter += 1
        }
    }

    /// KDF_RK per Signal §5.2: derives a new root key + new chain key from
    /// the current root key and a fresh DH output. HKDF-SHA256 over 64 bytes;
    /// first 32 = new root, second 32 = new chain key.
    private func kdfRk(rootKey: Data, dhOutput: Data) -> (newRoot: Data, newChain: Data) {
        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: dhOutput),
            salt: rootKey,
            info: hkdfRatchetInfo,
            outputByteCount: 64
        )
        let bytes = derived.withUnsafeBytes { Data($0) }
        let newRoot = bytes.subdata(in: 0 ..< 32)
        let newChain = bytes.subdata(in: 32 ..< 64)
        return (newRoot, newChain)
    }

    /// Lookup key for the skipped-message-keys table — Hex(remoteEphPub):counter.
    /// Matches the C# format exactly so the cache is conceptually identical
    /// (string is internal but easier to debug if you peek at session state).
    private func skippedKeyId(remoteEphPub: Data, counter: Int32) -> String {
        return "\(remoteEphPub.toHexUpper()):\(counter)"
    }

    // MARK: - Private helpers

    /// Computes the X25519 ECDH shared secret. RFC 7748 §6.1: detect the
    /// all-zero output (small-subgroup attack).
    private func x25519Agree(privateKey: Curve25519.KeyAgreement.PrivateKey, publicKey: Data) throws -> Data {
        let pub = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: publicKey)
        let shared = try privateKey.sharedSecretFromKeyAgreement(with: pub)
        let bytes = shared.withUnsafeBytes { Data($0) }
        if bytes.allSatisfy({ $0 == 0 }) {
            throw SignalProtocolError.lowOrderPoint
        }
        return bytes
    }

    /// HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# HKDF.DeriveKey
    /// for the X3DH root-key derivation.
    private func hkdf32(_ ikm: Data, info: Data) -> Data {
        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ikm),
            salt: Data(),
            info: info,
            outputByteCount: aesKeySize
        )
        return derived.withUnsafeBytes { Data($0) }
    }

    /// Single symmetric ratchet step (Signal §5.1).
    ///
    ///   message_key   = HMAC-SHA256(chain_key, 0x01)
    ///   new_chain_key = HMAC-SHA256(chain_key, 0x02)
    private func ratchetChainKey(_ chainKey: Data) -> (Data, Data) {
        let key = SymmetricKey(data: chainKey)
        let messageKey = Data(HMAC<SHA256>.authenticationCode(for: Data([0x01]), using: key))
        let newChain = Data(HMAC<SHA256>.authenticationCode(for: Data([0x02]), using: key))
        return (newChain, messageKey)
    }

    private func randomPositiveInt32() -> Int32 {
        var raw: Int32 = 0
        _ = withUnsafeMutableBytes(of: &raw) { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 4, buffer.baseAddress!)
        }
        let result = abs(raw)
        return result == 0 ? 1 : result
    }

    private func zero(_ data: Data) {
        var copy = data
        _ = copy.withUnsafeMutableBytes { buffer in
            memset(buffer.baseAddress!, 0, buffer.count)
        }
    }

    private func constantTimeEquals(_ a: Data, _ b: Data) -> Bool {
        if a.count != b.count { return false }
        var diff: UInt8 = 0
        for i in 0 ..< a.count {
            diff |= a[a.startIndex + i] ^ b[b.startIndex + i]
        }
        return diff == 0
    }
}

public enum SignalProtocolError: Error, Equatable {
    case noSessionEstablished(String)
    case signatureVerificationFailed
    case excessiveCounterGap(Int, Int)
    case ciphertextTooShort
    case failedToObtainMessageKey
    case invalidKeyFormat(String)
    case localUhidNotSet
    case preKeyMessageMissingMaterial
    case preKeyNotHeld(PreKeyKind, Int32)
    case lowOrderPoint

    public enum PreKeyKind: String {
        case signedPreKey
        case oneTimePreKey
    }
}

/// Internal hex helper for the skipped-keys cache lookup. Uppercase to
/// match C#'s `Convert.ToHexString` output exactly.
private extension Data {
    func toHexUpper() -> String {
        return self.map { String(format: "%02X", $0) }.joined()
    }
}

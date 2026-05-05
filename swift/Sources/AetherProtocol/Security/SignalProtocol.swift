// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Signal Protocol session state.
///
/// On the initiator side (we processed the peer's pre-key bundle), the
/// pending PreKey-message metadata is retained until the first message is
/// sent — that first message carries our X25519 identity key, our fresh
/// ephemeral public key, and the bundle ids we consumed, so the responder
/// can run X3DH on its side to derive the same root key.
internal class SignalSession {
    var rootKey: Data
    var sendChainKey: Data
    var recvChainKey: Data
    var sendCounter: Int32 = 0
    var recvCounter: Int32 = 0
    var skippedMessageKeys: [Int32: Data] = [:]

    var pendingPreKeyMessage: Bool = false
    var initiatorIdentityKeyX25519: Data = Data()
    var initiatorEphemeralKeyX25519: Data = Data()
    var usedSignedPreKeyId: Int32 = 0
    var usedOneTimePreKeyId: Int32 = 0

    init(rootKey: Data, sendChainKey: Data, recvChainKey: Data) {
        self.rootKey = rootKey
        self.sendChainKey = sendChainKey
        self.recvChainKey = recvChainKey
    }
}

/// Responder-side pre-key state. Holds the private halves of the signed
/// pre-key and one-time pre-keys so X3DH can be computed when an
/// initiator's PreKey message arrives.
internal struct PreKeyState {
    var signedPreKeyId: Int32 = 0
    var signedPreKeyPriv: Data = Data()
    var signedPreKeyPub: Data = Data()
    var signedPreKeySignature: Data = Data()

    /// id -> (priv, pub). Each entry is consumed (zeroed and removed) on first use.
    var oneTimePreKeys: [Int32: (priv: Data, pub: Data)] = [:]
}

/// Signal Protocol implementation: X3DH + Double-Ratchet.
///
/// Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
///   - DH1 = DH(IK_A, SPK_B) — long-term mutual auth
///   - DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity
///   - DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key
///   - DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (FS)
///
/// Root-key derivation: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
/// Symmetric ratchet: HMAC-SHA256, single-byte domain separation
///   (0x01 -> message key, 0x02 -> next chain key) per Signal §5.1.
/// Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
/// Identity signing: Ed25519 via Ed25519Service.
public actor SignalProtocolService {
    public static let maxSkippedKeys: Int = 1000

    public static let messageTypeNormal: Int32 = 0
    public static let messageTypePreKey: Int32 = 1

    private let aesNonceSize: Int = 12
    private let aesTagSize: Int = 16
    private let aesKeySize: Int = 32

    /// HKDF info strings — these MUST match the C# reference exactly. Any
    /// drift breaks cross-language interop (verified by
    /// fixtures/signal/expected/x3dh_basic.json).
    private let hkdfRootInfo = "aether-x3dh-root-v1".data(using: .utf8)!
    private let hkdfChainInitiatorSendInfo = "aether-chain-initiator-send-v1".data(using: .utf8)!
    private let hkdfChainInitiatorRecvInfo = "aether-chain-initiator-recv-v1".data(using: .utf8)!

    private var sessions: [String: SignalSession] = [:]

    // Long-term identity keys — two distinct keypairs per node.
    private var identityX25519Priv: Curve25519.KeyAgreement.PrivateKey
    private var identityX25519Pub: Data
    private var ed25519PrivateKey: Data
    private var ed25519PublicKey: Data

    private var localUhid: String?
    private var preKeys: PreKeyState = PreKeyState()

    public init() {
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
    /// X3DH is returned with messageType=PreKey and carries the X3DH inputs
    /// the responder needs to derive the same root key.
    public func encrypt(peerUhid: String, plaintext: Data) throws -> EncryptedPayload {
        guard let session = sessions[peerUhid] else {
            throw SignalProtocolError.noSessionEstablished(peerUhid)
        }
        guard let senderUhid = localUhid else {
            throw SignalProtocolError.localUhidNotSet
        }

        let (newChain, messageKey) = ratchetChainKey(session.sendChainKey)
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

        // PreKey message? Carry our X3DH inputs so the responder can mirror
        // the DHs and arrive at the same root key.
        if session.pendingPreKeyMessage {
            let payload = EncryptedPayload(
                ciphertext: combined,
                nonce: nonce,
                messageType: Self.messageTypePreKey,
                senderUhid: senderUhid,
                counter: counter,
                initiatorIdentityKeyX25519: session.initiatorIdentityKeyX25519,
                initiatorEphemeralKeyX25519: session.initiatorEphemeralKeyX25519,
                usedSignedPreKeyId: session.usedSignedPreKeyId,
                usedOneTimePreKeyId: session.usedOneTimePreKeyId
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
            counter: counter
        )
    }

    /// Decrypts a payload. If messageType=PreKey, establishes (or replaces)
    /// the responder-side session via mirrored X3DH first.
    public func decrypt(peerUhid: String, payload: EncryptedPayload) throws -> Data {
        if payload.messageType == Self.messageTypePreKey {
            guard let initiatorIK = payload.initiatorIdentityKeyX25519,
                  let initiatorEK = payload.initiatorEphemeralKeyX25519
            else {
                throw SignalProtocolError.preKeyMessageMissingMaterial
            }
            try establishResponderSession(
                peerUhid: peerUhid,
                initiatorIK: initiatorIK,
                initiatorEK: initiatorEK,
                usedSignedPreKeyId: payload.usedSignedPreKeyId,
                usedOneTimePreKeyId: payload.usedOneTimePreKeyId
            )
        }

        guard let session = sessions[peerUhid] else {
            throw SignalProtocolError.noSessionEstablished(peerUhid)
        }

        guard payload.ciphertext.count >= aesTagSize else {
            throw SignalProtocolError.ciphertextTooShort
        }

        var messageKey: Data
        if let skippedKey = session.skippedMessageKeys.removeValue(forKey: payload.counter) {
            messageKey = skippedKey
        } else {
            let gap = payload.counter - session.recvCounter
            if gap > Int32(Self.maxSkippedKeys) {
                throw SignalProtocolError.excessiveCounterGap(Int(gap), Self.maxSkippedKeys)
            }
            while session.recvCounter < payload.counter {
                let (newChain, skipKey) = ratchetChainKey(session.recvChainKey)
                session.recvChainKey = newChain
                session.skippedMessageKeys[session.recvCounter] = skipKey
                session.recvCounter += 1
            }
            let (newChain, key) = ratchetChainKey(session.recvChainKey)
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
    public func generatePreKeyBundle(localUhid: String) throws -> PreKeyBundle {
        self.localUhid = localUhid

        // One-time pre-key.
        let otpkPriv = Curve25519.KeyAgreement.PrivateKey()
        let otpkPub = otpkPriv.publicKey.rawRepresentation
        let preKeyId = randomPositiveInt32()
        preKeys.oneTimePreKeys[preKeyId] = (priv: otpkPriv.rawRepresentation, pub: otpkPub)

        // Signed pre-key.
        let spkPriv = Curve25519.KeyAgreement.PrivateKey()
        let spkPub = spkPriv.publicKey.rawRepresentation
        let signedPreKeyId = randomPositiveInt32()
        let signature = try Ed25519Service.sign(ed25519PrivateKey, spkPub)
        preKeys.signedPreKeyId = signedPreKeyId
        preKeys.signedPreKeyPriv = spkPriv.rawRepresentation
        preKeys.signedPreKeyPub = spkPub
        preKeys.signedPreKeySignature = signature

        return PreKeyBundle(
            uhid: localUhid,
            identityKey: ed25519PublicKey,
            identityKeyX25519: identityX25519Pub,
            preKeyId: preKeyId,
            preKey: otpkPub,
            signedPreKeyId: signedPreKeyId,
            signedPreKey: spkPub,
            signedPreKeySignature: signature
        )
    }

    /// Establishes initiator-side session via X3DH (Signal §3.3): generates
    /// a fresh ephemeral X25519 keypair, runs the four DHs, derives the root
    /// key, and primes the symmetric ratchet.
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

        let rootKey = hkdf(shared, info: hkdfRootInfo)
        let sendChain = hkdf(rootKey, info: hkdfChainInitiatorSendInfo)
        let recvChain = hkdf(rootKey, info: hkdfChainInitiatorRecvInfo)

        let session = SignalSession(rootKey: rootKey, sendChainKey: sendChain, recvChainKey: recvChain)
        session.pendingPreKeyMessage = true
        session.initiatorIdentityKeyX25519 = identityX25519Pub
        session.initiatorEphemeralKeyX25519 = ekPub
        session.usedSignedPreKeyId = bundle.signedPreKeyId
        session.usedOneTimePreKeyId = bundle.preKeyId

        sessions[bundle.uhid] = session
        zero(shared)
    }

    /// Mirrors the initiator's 4 X3DH DHs to derive the same root key, then
    /// derives chain keys with send/recv roles SWAPPED relative to the
    /// initiator. Consumes (and zeros) the one-time pre-key.
    private func establishResponderSession(
        peerUhid: String,
        initiatorIK: Data,
        initiatorEK: Data,
        usedSignedPreKeyId: Int32,
        usedOneTimePreKeyId: Int32
    ) throws {
        guard initiatorIK.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("initiator IK length \(initiatorIK.count) != 32")
        }
        guard initiatorEK.count == 32 else {
            throw SignalProtocolError.invalidKeyFormat("initiator EK length \(initiatorEK.count) != 32")
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
        let dh2 = try x25519Agree(privateKey: identityPrivKey, publicKey: initiatorEK)
        let dh3 = try x25519Agree(privateKey: spkPrivKey, publicKey: initiatorEK)
        let dh4 = try x25519Agree(privateKey: otpkPrivKey, publicKey: initiatorEK)

        var shared = Data()
        shared.append(dh1)
        shared.append(dh2)
        shared.append(dh3)
        shared.append(dh4)

        let rootKey = hkdf(shared, info: hkdfRootInfo)
        // SWAPPED: initiator's send-chain info derives our recv-chain.
        let recvChain = hkdf(rootKey, info: hkdfChainInitiatorSendInfo)
        let sendChain = hkdf(rootKey, info: hkdfChainInitiatorRecvInfo)

        sessions[peerUhid] = SignalSession(
            rootKey: rootKey,
            sendChainKey: sendChain,
            recvChainKey: recvChain
        )
        // Consume one-time pre-key — never reuse.
        preKeys.oneTimePreKeys.removeValue(forKey: usedOneTimePreKeyId)
        zero(shared)
    }

    public func getPublicKey() -> Data {
        ed25519PublicKey
    }

    public func getX25519PublicKey() -> Data {
        identityX25519Pub
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

    /// HKDF-SHA256 with no salt, fixed 32-byte output. Matches C# HKDF.DeriveKey.
    private func hkdf(_ ikm: Data, info: Data) -> Data {
        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ikm),
            salt: Data(),
            info: info,
            outputByteCount: aesKeySize
        )
        return derived.withUnsafeBytes { Data($0) }
    }

    /// Single Double-Ratchet step (Signal §5.1).
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

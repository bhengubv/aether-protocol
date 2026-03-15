// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Signal Protocol session state tracking.
internal class SignalSession {
    var rootKey: Data
    var sendChainKey: Data
    var recvChainKey: Data
    var sendCounter: Int32 = 0
    var recvCounter: Int32 = 0
    var remotePublicKey: Data
    var skippedMessageKeys: [Int32: Data] = [:]

    init(
        rootKey: Data,
        sendChainKey: Data,
        recvChainKey: Data,
        remotePublicKey: Data
    ) {
        self.rootKey = rootKey
        self.sendChainKey = sendChainKey
        self.recvChainKey = recvChainKey
        self.remotePublicKey = remotePublicKey
    }
}

/// Signal Protocol implementation providing end-to-end encryption for Aether mesh messaging.
///
/// Key agreement: X3DH with ECDH P-256.
/// Key derivation: HKDF-SHA256 with unique info strings per derivation context.
/// Encryption: AES-256-GCM with 12-byte nonce and 16-byte authentication tag.
/// Signing: Ed25519 via Ed25519Service.
///
/// The symmetric ratchet advances the chain key with each message sent or received.
/// Out-of-order messages are handled by caching skipped keys (up to MaxSkippedKeys).
public actor SignalProtocolService {
    public static let maxSkippedKeys: Int = 1000

    private let aesKeySize: Int = 32
    private let aesNonceSize: Int = 12
    private let aesTagSize: Int = 16

    private let hkdfRootInfo = "aether-root-v1".data(using: .utf8)!
    private let hkdfChainSendInfo = "aether-chain-send-v1".data(using: .utf8)!
    private let hkdfChainRecvInfo = "aether-chain-recv-v1".data(using: .utf8)!

    private var sessions: [String: SignalSession] = [:]
    private var identityPrivateKey: Data
    private var identityPublicKey: Data
    private var ed25519PrivateKey: Data
    private var ed25519PublicKey: Data

    public init() {
        (self.ed25519PrivateKey, self.ed25519PublicKey) = Ed25519Service.generateKeyPair()

        // Generate ECDH P-256 identity key pair
        let ecdhPrivateKey = P256.KeyAgreement.PrivateKey()
        self.identityPrivateKey = Data(ecdhPrivateKey.rawRepresentation)
        self.identityPublicKey = Data(ecdhPrivateKey.publicKey.rawRepresentation)
    }

    /// Checks if a session exists with a peer.
    public func hasSession(peerUhid: String) -> Bool {
        sessions[peerUhid] != nil
    }

    /// Encrypts plaintext for a peer.
    public func encrypt(peerUhid: String, plaintext: Data) throws -> EncryptedPayload {
        guard let session = sessions[peerUhid] else {
            throw SignalProtocolError.noSessionEstablished(peerUhid)
        }

        // Ratchet the sending chain to derive a message key
        let (newChainKey, messageKey) = try ratchetChainKey(session.sendChainKey, info: hkdfChainSendInfo)
        session.sendChainKey = newChainKey

        // Generate random nonce
        var nonce = Data(count: aesNonceSize)
        _ = nonce.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, aesNonceSize, buffer.baseAddress!)
        }

        // Encrypt with AES-GCM
        let sealedBox = try AES.GCM.seal(plaintext, using: SymmetricKey(data: messageKey), nonce: try AES.GCM.Nonce(data: nonce))

        // Combine ciphertext + tag
        var combined = Data()
        combined.append(sealedBox.ciphertext)
        combined.append(sealedBox.tag)

        let counter = session.sendCounter
        session.sendCounter += 1

        // Zero the message key
        var zeroKey = messageKey
        _ = zeroKey.withUnsafeMutableBytes { buffer in
            memset(buffer.baseAddress!, 0, buffer.count)
        }

        return EncryptedPayload(
            ciphertext: combined,
            nonce: nonce,
            messageType: 0,
            senderUhid: peerUhid,
            counter: counter
        )
    }

    /// Decrypts a payload from a peer.
    public func decrypt(peerUhid: String, payload: EncryptedPayload) throws -> Data {
        guard let session = sessions[peerUhid] else {
            throw SignalProtocolError.noSessionEstablished(peerUhid)
        }

        var messageKey: Data?

        // Check if this is a skipped message
        if let skippedKey = session.skippedMessageKeys.removeValue(forKey: payload.counter) {
            messageKey = skippedKey
        } else {
            // Check for excessive counter gap
            let gap = payload.counter - session.recvCounter
            if gap > Int32(Self.maxSkippedKeys) {
                throw SignalProtocolError.excessiveCounterGap(Int(gap), Self.maxSkippedKeys)
            }

            // Skip ahead and cache intermediate keys
            while session.recvCounter < payload.counter {
                let (newChainKey, skipKey) = try ratchetChainKey(session.recvChainKey, info: hkdfChainRecvInfo)
                session.recvChainKey = newChainKey
                session.skippedMessageKeys[session.recvCounter] = skipKey
                session.recvCounter += 1
            }

            // Derive the actual message key
            let (newChainKey, key) = try ratchetChainKey(session.recvChainKey, info: hkdfChainRecvInfo)
            session.recvChainKey = newChainKey
            messageKey = key
            session.recvCounter += 1
        }

        guard let key = messageKey else {
            throw SignalProtocolError.failedToObtainMessageKey
        }

        // Extract ciphertext and tag
        guard payload.ciphertext.count >= aesTagSize else {
            throw SignalProtocolError.ciphertextTooShort
        }

        let ciphertextLength = payload.ciphertext.count - aesTagSize
        let ciphertext = payload.ciphertext.subdata(in: 0 ..< ciphertextLength)
        let tag = payload.ciphertext.subdata(in: ciphertextLength ..< payload.ciphertext.count)

        // Decrypt with AES-GCM
        let sealedBox = try AES.GCM.SealedBox(nonce: try AES.GCM.Nonce(data: payload.nonce), ciphertext: ciphertext, tag: tag)
        let plaintext = try AES.GCM.open(sealedBox, using: SymmetricKey(data: key))

        // Zero the message key
        var zeroKey = key
        _ = zeroKey.withUnsafeMutableBytes { buffer in
            memset(buffer.baseAddress!, 0, buffer.count)
        }

        return plaintext
    }

    /// Generates a pre-key bundle for session establishment.
    public func generatePreKeyBundle(localUhid: String) throws -> PreKeyBundle {
        // Generate one-time pre-key (ECDH P-256)
        let preKeyEcdh = P256.KeyAgreement.PrivateKey()
        let preKeyPublic = Data(preKeyEcdh.publicKey.rawRepresentation)
        var preKeyId: Int32 = 0
        _ = withUnsafeMutableBytes(of: &preKeyId) { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 4, buffer.baseAddress!)
        }
        // Ensure positive
        preKeyId = abs(preKeyId)
        if preKeyId == 0 { preKeyId = 1 }

        // Generate signed pre-key (ECDH P-256)
        let signedPreKeyEcdh = P256.KeyAgreement.PrivateKey()
        let signedPreKeyPublic = Data(signedPreKeyEcdh.publicKey.rawRepresentation)
        var signedPreKeyId: Int32 = 0
        _ = withUnsafeMutableBytes(of: &signedPreKeyId) { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 4, buffer.baseAddress!)
        }
        // Ensure positive
        signedPreKeyId = abs(signedPreKeyId)
        if signedPreKeyId == 0 { signedPreKeyId = 1 }

        // Sign the signed pre-key with our Ed25519 identity key
        let signature = try Ed25519Service.sign(ed25519PrivateKey, signedPreKeyPublic)

        return PreKeyBundle(
            uhid: localUhid,
            identityKey: ed25519PublicKey,
            preKeyId: preKeyId,
            preKey: preKeyPublic,
            signedPreKeyId: signedPreKeyId,
            signedPreKey: signedPreKeyPublic,
            signedPreKeySignature: signature
        )
    }

    /// Processes a pre-key bundle and establishes a session.
    public func processPreKeyBundle(_ bundle: PreKeyBundle) throws {
        // Verify the signed pre-key signature
        guard Ed25519Service.verify(bundle.identityKey, bundle.signedPreKey, bundle.signedPreKeySignature) else {
            throw SignalProtocolError.signatureVerificationFailed
        }

        // X3DH key agreement
        let sharedSecret = try performX3DH(
            remoteSignedPreKey: bundle.signedPreKey,
            remotePreKey: bundle.preKey
        )

        // Derive root key and initial chain keys using HKDF
        let rootKey = try deriveKey(sharedSecret, info: hkdfRootInfo)
        let sendChainKey = try deriveKey(rootKey, info: hkdfChainSendInfo)
        let recvChainKey = try deriveKey(rootKey, info: hkdfChainRecvInfo)

        let session = SignalSession(
            rootKey: rootKey,
            sendChainKey: sendChainKey,
            recvChainKey: recvChainKey,
            remotePublicKey: bundle.identityKey
        )

        sessions[bundle.uhid] = session

        // Zero the shared secret
        var zeroSecret = sharedSecret
        _ = zeroSecret.withUnsafeMutableBytes { buffer in
            memset(buffer.baseAddress!, 0, buffer.count)
        }
    }

    /// Gets the Ed25519 public key for this node.
    public func getPublicKey() -> Data {
        ed25519PublicKey
    }

    // MARK: - Private Methods

    private func performX3DH(remoteSignedPreKey: Data, remotePreKey: Data) throws -> Data {
        let localEcdh = try P256.KeyAgreement.PrivateKey(rawRepresentation: identityPrivateKey)
        let remoteSignedKey = try P256.KeyAgreement.PublicKey(rawRepresentation: remoteSignedPreKey)
        let remoteOnetimeKey = try P256.KeyAgreement.PublicKey(rawRepresentation: remotePreKey)

        // DH1: identity <-> signed pre-key
        let dh1 = try localEcdh.sharedSecretFromKeyAgreement(with: remoteSignedKey)

        // DH2: identity <-> one-time pre-key
        let dh2 = try localEcdh.sharedSecretFromKeyAgreement(with: remoteOnetimeKey)

        // Concatenate DH results
        var combined = Data()
        combined.append(dh1.withUnsafeBytes { Data($0) })
        combined.append(dh2.withUnsafeBytes { Data($0) })

        return combined
    }

    private func deriveKey(_ inputKeyMaterial: Data, info: Data) throws -> Data {
        let emptyData = Data()
        let derivedBytes = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: inputKeyMaterial),
            salt: emptyData,
            info: info,
            outputByteCount: aesKeySize
        )
        return Data(derivedBytes.withUnsafeBytes { buffer in
            Array(buffer.prefix(aesKeySize))
        })
    }

    private func ratchetChainKey(_ chainKey: Data, info: Data) throws -> (Data, Data) {
        // HMAC-based ratchet: messageKey = HMAC-SHA256(chainKey, 0x01 || counter)
        let counterData = Data([0x01])
        let messageKeyBytes = HMAC<SHA256>.authenticationCode(for: counterData, using: SymmetricKey(data: chainKey))
        let messageKey = Data(messageKeyBytes)

        // Advance chain key: newChainKey = HMAC-SHA256(chainKey, 0x02 || counter)
        let advanceData = Data([0x02])
        let newChainKeyBytes = HMAC<SHA256>.authenticationCode(for: advanceData, using: SymmetricKey(data: chainKey))
        let newChainKey = Data(newChainKeyBytes)

        return (newChainKey, messageKey)
    }
}

public enum SignalProtocolError: Error, Equatable {
    case noSessionEstablished(String)
    case signatureVerificationFailed
    case excessiveCounterGap(Int, Int)
    case ciphertextTooShort
    case failedToObtainMessageKey
    case invalidKeyFormat(String)
}

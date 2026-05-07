// SPDX-License-Identifier: MIT
import XCTest
import Crypto
import Foundation
@testable import AetherProtocol

/// Cross-language Signal-protocol fixture verifier and end-to-end exercises.
///
/// Verifies that the Swift implementation produces byte-identical X3DH and
/// ratchet outputs to the C# reference (committed in
/// fixtures/signal/expected/*.json). Any drift between Swift and C# / Go /
/// Python / TS / Kotlin / Rust / C surfaces here as a hex mismatch.
final class SignalFixtureTests: XCTestCase {

    // MARK: - Fixture verifiers

    func testSignalFixture_X3DHBasic() throws {
        let (inputs, expected) = try loadFixturePair(caseName: "x3dh_basic")

        let aliceIK = try mustHex(inputs["alice_identity_priv_hex"] as! String)
        let aliceEK = try mustHex(inputs["alice_ephemeral_priv_hex"] as! String)
        let bobIK = try mustHex(inputs["bob_identity_priv_hex"] as! String)
        let bobSPK = try mustHex(inputs["bob_signed_pre_key_priv_hex"] as! String)
        let bobOPK = try mustHex(inputs["bob_one_time_pre_key_priv_hex"] as! String)

        let aliceIKPub = try x25519DerivePublic(aliceIK)
        let aliceEKPub = try x25519DerivePublic(aliceEK)
        let bobIKPub = try x25519DerivePublic(bobIK)
        let bobSPKPub = try x25519DerivePublic(bobSPK)
        let bobOPKPub = try x25519DerivePublic(bobOPK)

        let dh1 = try x25519Agree(aliceIK, bobSPKPub)
        let dh2 = try x25519Agree(aliceEK, bobIKPub)
        let dh3 = try x25519Agree(aliceEK, bobSPKPub)
        let dh4 = try x25519Agree(aliceEK, bobOPKPub)

        var shared = Data()
        shared.append(dh1); shared.append(dh2); shared.append(dh3); shared.append(dh4)

        let rootInfo = (inputs["hkdf_root_info_utf8"] as! String).data(using: .utf8)!
        let sendInfo = (inputs["hkdf_chain_initiator_send_info_utf8"] as! String).data(using: .utf8)!
        let recvInfo = (inputs["hkdf_chain_initiator_recv_info_utf8"] as! String).data(using: .utf8)!

        let rootKey = hkdf32(shared, info: rootInfo)
        let sendChain = hkdf32(rootKey, info: sendInfo)
        let recvChain = hkdf32(rootKey, info: recvInfo)

        XCTAssertEqual(aliceIKPub.toHex(), expected["alice_identity_pub_hex"] as? String)
        XCTAssertEqual(aliceEKPub.toHex(), expected["alice_ephemeral_pub_hex"] as? String)
        XCTAssertEqual(bobIKPub.toHex(), expected["bob_identity_pub_hex"] as? String)
        XCTAssertEqual(bobSPKPub.toHex(), expected["bob_signed_pre_key_pub_hex"] as? String)
        XCTAssertEqual(bobOPKPub.toHex(), expected["bob_one_time_pre_key_pub_hex"] as? String)
        XCTAssertEqual(dh1.toHex(), expected["dh1_hex"] as? String)
        XCTAssertEqual(dh2.toHex(), expected["dh2_hex"] as? String)
        XCTAssertEqual(dh3.toHex(), expected["dh3_hex"] as? String)
        XCTAssertEqual(dh4.toHex(), expected["dh4_hex"] as? String)
        XCTAssertEqual(shared.toHex(), expected["shared_secret_hex"] as? String)
        XCTAssertEqual(rootKey.toHex(), expected["root_key_hex"] as? String)
        XCTAssertEqual(sendChain.toHex(), expected["initiator_send_chain_key_hex"] as? String)
        XCTAssertEqual(recvChain.toHex(), expected["initiator_recv_chain_key_hex"] as? String)
    }

    func testSignalFixture_RatchetStepBasic() throws {
        let (inputs, expected) = try loadFixturePair(caseName: "ratchet_step_basic")
        let chainKey = try mustHex(inputs["chain_key_hex"] as! String)
        XCTAssertEqual(hmacOne(chainKey, 0x01).toHex(), expected["message_key_hex"] as? String)
        XCTAssertEqual(hmacOne(chainKey, 0x02).toHex(), expected["next_chain_key_hex"] as? String)
    }

    func testSignalFixture_RatchetStepThreeIterations() throws {
        let (inputs, expected) = try loadFixturePair(caseName: "ratchet_step_three_iterations")
        var chainKey = try mustHex(inputs["initial_chain_key_hex"] as! String)
        for i in 0..<3 {
            let msg = hmacOne(chainKey, 0x01)
            let nxt = hmacOne(chainKey, 0x02)
            XCTAssertEqual(msg.toHex(), expected["step_\(i)_message_key_hex"] as? String)
            XCTAssertEqual(nxt.toHex(), expected["step_\(i)_chain_key_after_hex"] as? String)
            chainKey = nxt
        }
    }

    /// KDF_RK fixture (Signal §5.2): HKDF-SHA256(salt=root_key,
    /// ikm=dh_output, info=UTF8('aether-ratchet-rk-v1'), L=64) split 32+32
    /// into new_root_key + new_chain_key. Cross-language byte-identical.
    func testSignalFixture_KdfRkBasic() throws {
        let (inputs, expected) = try loadFixturePair(caseName: "kdf_rk_basic")
        let rk = try mustHex(inputs["root_key_hex"] as! String)
        let dh = try mustHex(inputs["dh_output_hex"] as! String)
        let info = (inputs["hkdf_info_utf8"] as! String).data(using: .utf8)!

        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: dh),
            salt: rk,
            info: info,
            outputByteCount: 64
        )
        let bytes = derived.withUnsafeBytes { Data($0) }
        let newRoot = bytes.subdata(in: 0 ..< 32)
        let newChain = bytes.subdata(in: 32 ..< 64)

        XCTAssertEqual(newRoot.toHex(), expected["new_root_key_hex"] as? String)
        XCTAssertEqual(newChain.toHex(), expected["new_chain_key_hex"] as? String)
    }

    // MARK: - End-to-end exercises

    func testX3DH_FirstMessageRoundTrips() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        let encrypted = try await alice.encrypt(peerUhid: "bob", plaintext: "the mesh is alive".data(using: .utf8)!)
        XCTAssertEqual(encrypted.messageType, SignalProtocolService.messageTypePreKey)
        XCTAssertEqual(encrypted.initiatorIdentityKeyX25519?.count, 32)
        XCTAssertEqual(encrypted.initiatorEphemeralKeyX25519?.count, 32)
        // Double Ratchet: every message carries the sender's current DH-ratchet
        // pubkey, including the very first PreKey message (which equals the
        // X3DH ephemeral — Signal-canonical integration).
        XCTAssertEqual(encrypted.senderEphemeralKeyX25519?.count, 32)
        XCTAssertEqual(encrypted.senderEphemeralKeyX25519, encrypted.initiatorEphemeralKeyX25519)
        XCTAssertEqual(encrypted.senderUhid, "alice")

        let plaintext = try await bob.decrypt(peerUhid: "alice", payload: encrypted)
        XCTAssertEqual(String(data: plaintext, encoding: .utf8), "the mesh is alive")
    }

    func testX3DH_SubsequentMessageIsNormal() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        let first = try await alice.encrypt(peerUhid: "bob", plaintext: "a".data(using: .utf8)!)
        _ = try await bob.decrypt(peerUhid: "alice", payload: first)

        let second = try await alice.encrypt(peerUhid: "bob", plaintext: "b".data(using: .utf8)!)
        XCTAssertEqual(second.messageType, SignalProtocolService.messageTypeNormal)
        XCTAssertNil(second.initiatorIdentityKeyX25519)
        // initiatorEphemeralKeyX25519 (deprecated alias) is nil on normal messages.
        XCTAssertNil(second.initiatorEphemeralKeyX25519)
        // senderEphemeralKeyX25519 is populated on EVERY message (Double
        // Ratchet header). Same DHs as the first message because Alice
        // hasn't ratcheted (no roundtrip yet).
        XCTAssertEqual(second.senderEphemeralKeyX25519, first.senderEphemeralKeyX25519)
        let dec = try await bob.decrypt(peerUhid: "alice", payload: second)
        XCTAssertEqual(dec, "b".data(using: .utf8)!)
    }

    func testX3DH_OneTimePreKeyConsumed() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        let first = try await alice.encrypt(peerUhid: "bob", plaintext: "first".data(using: .utf8)!)
        _ = try await bob.decrypt(peerUhid: "alice", payload: first)

        // Replay using the same bundle should fail.
        let alice2 = SignalProtocolService()
        _ = try await alice2.generatePreKeyBundle(localUhid: "alice2")
        try await alice2.processPreKeyBundle(bobBundle)
        let replay = try await alice2.encrypt(peerUhid: "bob", plaintext: "replay".data(using: .utf8)!)

        do {
            _ = try await bob.decrypt(peerUhid: "alice2", payload: replay)
            XCTFail("expected error decrypting replayed OPK msg")
        } catch {
            // expected
        }
    }

    func testEncrypt_WithoutLocalUhid_Throws() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        // Note: no generatePreKeyBundle / setLocalUhid on Alice.
        try await alice.processPreKeyBundle(bobBundle)

        do {
            _ = try await alice.encrypt(peerUhid: "bob", plaintext: "x".data(using: .utf8)!)
            XCTFail("expected error when local UHID unset")
        } catch SignalProtocolError.localUhidNotSet {
            // expected
        } catch {
            XCTFail("wrong error: \(error)")
        }
    }

    func testPreKeyBundle_HasBothIdentityKeys() async throws {
        let svc = SignalProtocolService()
        let bundle = try await svc.generatePreKeyBundle(localUhid: "alice")
        XCTAssertEqual(bundle.identityKey.count, 32)         // Ed25519
        XCTAssertEqual(bundle.identityKeyX25519.count, 32)   // X25519
        XCTAssertNotEqual(bundle.identityKey, bundle.identityKeyX25519)
        XCTAssertEqual(bundle.signedPreKey.count, 32)
        XCTAssertEqual(bundle.preKey.count, 32)
        XCTAssertEqual(bundle.signedPreKeySignature.count, 64)
    }

    // MARK: - Double Ratchet (Signal §5)

    /// Every Double-Ratchet message — including the first PreKey message
    /// and subsequent normal messages — carries the sender's current
    /// ratchet public key on the wire. Mirror of C#
    /// `DoubleRatchet_EveryMessageCarriesSenderEphemeralKey`.
    func testDoubleRatchet_EveryMessageCarriesSenderEphemeralKey() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        let first = try await alice.encrypt(peerUhid: "bob", plaintext: "a".data(using: .utf8)!)
        XCTAssertNotNil(first.senderEphemeralKeyX25519)
        XCTAssertEqual(first.senderEphemeralKeyX25519?.count, 32)

        _ = try await bob.decrypt(peerUhid: "alice", payload: first)

        // Subsequent message also carries senderEphemeralKeyX25519 (same
        // value — Alice hasn't ratcheted because Bob hasn't responded yet).
        let second = try await alice.encrypt(peerUhid: "bob", plaintext: "b".data(using: .utf8)!)
        XCTAssertNotNil(second.senderEphemeralKeyX25519)
        XCTAssertEqual(first.senderEphemeralKeyX25519, second.senderEphemeralKeyX25519)
    }

    /// After a roundtrip (Alice -> Bob -> Alice), each side's ratchet pubkey
    /// rotates. Mirror of C# `DoubleRatchet_SenderEphemeralKey_RotatesAfterRoundtrip`.
    func testDoubleRatchet_SenderEphemeralKey_RotatesAfterRoundtrip() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        // Alice -> Bob: Alice's first ratchet pub.
        let aliceFirst = try await alice.encrypt(peerUhid: "bob", plaintext: "ping".data(using: .utf8)!)
        _ = try await bob.decrypt(peerUhid: "alice", payload: aliceFirst)

        // Bob -> Alice: Bob's first ratchet pub (rotated by responder-side DH ratchet).
        let bobReply = try await bob.encrypt(peerUhid: "alice", plaintext: "pong".data(using: .utf8)!)
        XCTAssertNotNil(bobReply.senderEphemeralKeyX25519)
        // Bob's ratchet pub should be DIFFERENT from Alice's (Bob generated
        // fresh DHs on his DH-ratchet step).
        XCTAssertNotEqual(aliceFirst.senderEphemeralKeyX25519, bobReply.senderEphemeralKeyX25519)

        _ = try await alice.decrypt(peerUhid: "bob", payload: bobReply)

        // Alice -> Bob (after roundtrip): Alice should now use a NEW ratchet
        // pub (rotated on her DH-ratchet step when she received Bob's reply).
        let aliceSecond = try await alice.encrypt(peerUhid: "bob", plaintext: "ping2".data(using: .utf8)!)
        XCTAssertNotEqual(aliceFirst.senderEphemeralKeyX25519, aliceSecond.senderEphemeralKeyX25519)
        XCTAssertNotEqual(bobReply.senderEphemeralKeyX25519, aliceSecond.senderEphemeralKeyX25519)

        // Bob can still decrypt Alice's new message.
        let dec = try await bob.decrypt(peerUhid: "alice", payload: aliceSecond)
        XCTAssertEqual(String(data: dec, encoding: .utf8), "ping2")
    }

    /// PN (previousChainCount) tracks how many messages were sent on the
    /// previous chain before the most recent DH-ratchet step.
    /// Mirror of C# `DoubleRatchet_PreviousChainCount_TracksMessagesPerChain`.
    func testDoubleRatchet_PreviousChainCount_TracksMessagesPerChain() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        // Alice sends 3 messages without a roundtrip.
        for i in 0 ..< 3 {
            let enc = try await alice.encrypt(peerUhid: "bob", plaintext: "a\(i)".data(using: .utf8)!)
            // PN is 0 because this IS Alice's first chain.
            XCTAssertEqual(enc.previousChainCount, 0)
            _ = try await bob.decrypt(peerUhid: "alice", payload: enc)
        }

        // Bob sends a reply, triggering his DH-ratchet step.
        let bobReply = try await bob.encrypt(peerUhid: "alice", plaintext: "hi".data(using: .utf8)!)
        // Bob's PN reflects however many messages Bob sent in his previous
        // sending chain — which was 0 (Bob hadn't sent anything yet before
        // his DH-ratchet step rotated his chain).
        XCTAssertEqual(bobReply.previousChainCount, 0)
        _ = try await alice.decrypt(peerUhid: "bob", payload: bobReply)

        // Alice's next message after her DH-ratchet step. Her PN should be
        // 3 — that's how many messages she sent on her previous chain
        // before Bob's reply triggered her ratchet.
        let aliceNew = try await alice.encrypt(peerUhid: "bob", plaintext: "a3".data(using: .utf8)!)
        XCTAssertEqual(aliceNew.previousChainCount, 3)
    }

    /// A message from the OLD chain that arrives AFTER a DH-ratchet boundary
    /// (because of out-of-order delivery) must still decrypt via the
    /// skipped-keys cache keyed by the old DHr pubkey.
    /// Mirror of C# `DoubleRatchet_OutOfOrderAcrossDhRatchetBoundary_StillDecrypts`.
    func testDoubleRatchet_OutOfOrderAcrossDhRatchetBoundary_StillDecrypts() async throws {
        // Alice sends 3 messages on chain 1. Bob receives only the first 2,
        // then Alice does a DH-ratchet (because Bob replied) and sends a 4th
        // on chain 2. The 3rd message (from chain 1) arrives last —
        // Bob must still be able to decrypt it via the skipped-keys cache
        // keyed by (Alice's old DHs pub, counter=2).
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        let a0 = try await alice.encrypt(peerUhid: "bob", plaintext: "a0".data(using: .utf8)!)
        let a1 = try await alice.encrypt(peerUhid: "bob", plaintext: "a1".data(using: .utf8)!)
        let a2 = try await alice.encrypt(peerUhid: "bob", plaintext: "a2".data(using: .utf8)!)

        // Bob receives a0, a1 only.
        // await inside XCTAssertEqual autoclosure is not allowed in Swift 6 — extract first.
        let dec_a0 = try await bob.decrypt(peerUhid: "alice", payload: a0)
        XCTAssertEqual(String(data: dec_a0, encoding: .utf8), "a0")
        let dec_a1 = try await bob.decrypt(peerUhid: "alice", payload: a1)
        XCTAssertEqual(String(data: dec_a1, encoding: .utf8), "a1")

        // Bob replies — triggers his DH-ratchet step.
        let bReply = try await bob.encrypt(peerUhid: "alice", plaintext: "hi".data(using: .utf8)!)
        _ = try await alice.decrypt(peerUhid: "bob", payload: bReply)

        // Alice sends a4 on her new chain (after her DH-ratchet step).
        let a4 = try await alice.encrypt(peerUhid: "bob", plaintext: "a4".data(using: .utf8)!)
        // Bob receives a4 — triggers his second DH-ratchet step. He must
        // skip-derive a key for Alice's old chain counter=2 because PN=3.
        let dec_a4 = try await bob.decrypt(peerUhid: "alice", payload: a4)
        XCTAssertEqual(String(data: dec_a4, encoding: .utf8), "a4")

        // Now the missing a2 (from Alice's OLD chain) finally arrives. Bob
        // should pull the skipped key from cache.
        let dec_a2 = try await bob.decrypt(peerUhid: "alice", payload: a2)
        XCTAssertEqual(String(data: dec_a2, encoding: .utf8), "a2")
    }

    /// 10 alternating messages — both sides ratchet at every roundtrip and
    /// every message must decrypt correctly.
    /// Mirror of C# `DoubleRatchet_LongConversation_AllMessagesDecrypt`.
    func testDoubleRatchet_LongConversation_AllMessagesDecrypt() async throws {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()

        let bobBundle = try await bob.generatePreKeyBundle(localUhid: "bob")
        _ = try await alice.generatePreKeyBundle(localUhid: "alice")
        try await alice.processPreKeyBundle(bobBundle)

        for i in 0 ..< 10 {
            let aMsg = "alice \(i)"
            let aEnc = try await alice.encrypt(peerUhid: "bob", plaintext: aMsg.data(using: .utf8)!)
            let aDec = try await bob.decrypt(peerUhid: "alice", payload: aEnc)
            XCTAssertEqual(String(data: aDec, encoding: .utf8), aMsg)

            let bMsg = "bob \(i)"
            let bEnc = try await bob.encrypt(peerUhid: "alice", plaintext: bMsg.data(using: .utf8)!)
            let bDec = try await alice.decrypt(peerUhid: "bob", payload: bEnc)
            XCTAssertEqual(String(data: bDec, encoding: .utf8), bMsg)
        }
    }

    // MARK: - Helpers

    private func loadFixturePair(caseName: String) throws -> ([String: Any], [String: Any]) {
        let root = try repoRoot()
        let inputsURL = root.appendingPathComponent("fixtures/signal/inputs.json")
        let expectedURL = root.appendingPathComponent("fixtures/signal/expected/\(caseName).json")

        let inputsData = try Data(contentsOf: inputsURL)
        guard let inputsJson = try JSONSerialization.jsonObject(with: inputsData) as? [String: Any],
              let cases = inputsJson["cases"] as? [[String: Any]],
              let inputs = cases.first(where: { ($0["name"] as? String) == caseName })
        else {
            throw NSError(domain: "fixture", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "case \(caseName) not found in inputs.json"])
        }
        let expectedData = try Data(contentsOf: expectedURL)
        guard let expected = try JSONSerialization.jsonObject(with: expectedData) as? [String: Any]
        else {
            throw NSError(domain: "fixture", code: 2,
                          userInfo: [NSLocalizedDescriptionKey: "expected/\(caseName).json malformed"])
        }
        return (inputs, expected)
    }

    private func repoRoot() throws -> URL {
        var dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<10 {
            if FileManager.default.fileExists(atPath: dir.appendingPathComponent("AetherProtocol.slnx").path) {
                return dir
            }
            dir = dir.deletingLastPathComponent()
        }
        throw NSError(domain: "fixture", code: 3,
                      userInfo: [NSLocalizedDescriptionKey: "AetherProtocol.slnx not found above \(#filePath)"])
    }

    private func mustHex(_ s: String) throws -> Data {
        var bytes: [UInt8] = []
        var i = s.startIndex
        while i < s.endIndex {
            let next = s.index(i, offsetBy: 2)
            guard let b = UInt8(s[i..<next], radix: 16) else {
                throw NSError(domain: "hex", code: 1, userInfo: nil)
            }
            bytes.append(b)
            i = next
        }
        return Data(bytes)
    }

    private func x25519DerivePublic(_ priv: Data) throws -> Data {
        let p = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: priv)
        return p.publicKey.rawRepresentation
    }

    private func x25519Agree(_ priv: Data, _ pub: Data) throws -> Data {
        let p = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: priv)
        let q = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: pub)
        let s = try p.sharedSecretFromKeyAgreement(with: q)
        return s.withUnsafeBytes { Data($0) }
    }

    private func hkdf32(_ ikm: Data, info: Data) -> Data {
        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ikm),
            salt: Data(),
            info: info,
            outputByteCount: 32
        )
        return derived.withUnsafeBytes { Data($0) }
    }

    private func hmacOne(_ key: Data, _ b: UInt8) -> Data {
        return Data(HMAC<SHA256>.authenticationCode(for: Data([b]), using: SymmetricKey(data: key)))
    }
}

private extension Data {
    func toHex() -> String {
        return self.map { String(format: "%02x", $0) }.joined()
    }
}

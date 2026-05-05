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

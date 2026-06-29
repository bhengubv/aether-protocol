// SPDX-License-Identifier: MIT

import Foundation

/// Derives a libp2p **PeerID** from a node's Ed25519 public key — the bridge between an AetherNet
/// identity and the global libp2p relay / DHT used by the decentralised relay layer.
///
/// Because AetherNet and libp2p both key identity off the same Ed25519 public key, the PeerID is a
/// *pure, deterministic* function of that key — no lookup table, no network. A node can compute its
/// own PeerID (to announce on the libp2p DHT) and any peer's PeerID (to dial it) from the public
/// key alone.
///
/// ## Encoding (byte-identical across every SDK language)
///   1. protobuf PublicKey = `08 01` (field 1 Type = Ed25519) `12 20` (field 2 Data, length 32)
///      followed by the 32-byte key — 36 bytes total.
///   2. identity multihash = `00` (identity hash code) `24` (length 36) followed by the protobuf —
///      38 bytes. libp2p uses the identity multihash for keys whose serialized form is ≤ 42 bytes,
///      which Ed25519 always is.
///   3. PeerID string = base58btc (Bitcoin alphabet) of the 38-byte multihash, WITHOUT a multibase
///      prefix. Always renders as `12D3Koo…` for Ed25519.
///
/// Verified byte-for-byte against real `js-libp2p` output; see `fixtures/peerid/`.
public enum PeerId {

    // MARK: - Errors

    public enum PeerIdError: Error, Equatable {
        /// The supplied public key was not exactly ``ed25519PublicKeyLength`` (32) bytes.
        case invalidPublicKeyLength(Int)
    }

    // MARK: - Constants

    /// Bitcoin base58 alphabet (no 0, O, I, l).
    private static let base58Alphabet: [Character] =
        Array("123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz")

    /// identity-multihash(code 0x00, len 0x24 = 36) || protobuf PublicKey(type Ed25519: 0x08 0x01;
    /// data len 32: 0x12 0x20).
    private static let ed25519Prefix: [UInt8] = [0x00, 0x24, 0x08, 0x01, 0x12, 0x20]

    /// Length in bytes of a raw Ed25519 public key.
    public static let ed25519PublicKeyLength: Int = 32

    // MARK: - Derivation

    /// Returns the libp2p PeerID string (e.g. `12D3Koo…`) for a 32-byte Ed25519 public key.
    /// - Throws: ``PeerIdError/invalidPublicKeyLength(_:)`` when `publicKey` is not exactly 32 bytes.
    public static func fromEd25519PublicKey(_ publicKey: [UInt8]) throws -> String {
        guard publicKey.count == ed25519PublicKeyLength else {
            throw PeerIdError.invalidPublicKeyLength(publicKey.count)
        }
        var multihash = [UInt8]()
        multihash.reserveCapacity(ed25519Prefix.count + ed25519PublicKeyLength)
        multihash.append(contentsOf: ed25519Prefix)
        multihash.append(contentsOf: publicKey)
        return base58Encode(multihash)
    }

    // MARK: - Private

    /// Standard base58 (bitcoinj algorithm) — preserves leading zero bytes as leading '1's.
    private static func base58Encode(_ input: [UInt8]) -> String {
        if input.isEmpty { return "" }

        var zeros = 0
        while zeros < input.count && input[zeros] == 0 {
            zeros += 1
        }

        var buffer = input // divmod mutates in place
        var encoded = [Character](repeating: base58Alphabet[0], count: input.count * 2) // safe upper bound
        var outputStart = encoded.count

        var inputStart = zeros
        while inputStart < buffer.count {
            outputStart -= 1
            encoded[outputStart] = base58Alphabet[Int(divMod58(&buffer, firstDigit: inputStart))]
            if buffer[inputStart] == 0 {
                inputStart += 1 // a digit fully consumed
            }
        }
        // Drop extra leading '1's the loop may have produced.
        while outputStart < encoded.count && encoded[outputStart] == base58Alphabet[0] {
            outputStart += 1
        }
        // Re-add one '1' per leading zero byte of the input.
        var remainingZeros = zeros
        while remainingZeros > 0 {
            outputStart -= 1
            encoded[outputStart] = base58Alphabet[0]
            remainingZeros -= 1
        }

        return String(encoded[outputStart..<encoded.count])
    }

    /// Divides the big-endian base-256 number in `number[firstDigit...]` by 58, in place, returning
    /// the remainder. Bytes are treated as unsigned.
    private static func divMod58(_ number: inout [UInt8], firstDigit: Int) -> Int {
        var remainder = 0
        for i in firstDigit..<number.count {
            let digit = Int(number[i]) // UInt8 is already unsigned
            let temp = remainder * 256 + digit
            number[i] = UInt8(temp / 58)
            remainder = temp % 58
        }
        return remainder
    }
}

// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Errors raised by the BIP-39 codec and identity backup/restore.
///
/// Mirrors the failure surface of the C# reference
/// (`AetherNet.Security.Backup.Bip39Mnemonic` / `IdentityBackup`): an argument
/// error for out-of-range entropy, and a format error for a malformed phrase,
/// an unknown word, a wrong word count, or a checksum mismatch — so a mistyped
/// phrase is rejected rather than silently yielding the wrong secret.
public enum Bip39Error: Error, Equatable {
    /// Entropy was not 16, 20, 24, 28, or 32 bytes.
    case invalidEntropyLength(Int)
    /// The mnemonic did not have 12, 15, 18, 21, or 24 words.
    case invalidWordCount(Int)
    /// A word in the mnemonic is not in the BIP-39 English wordlist.
    case unknownWord(String)
    /// The SHA-256 checksum embedded in the mnemonic did not match.
    case invalidChecksum
    /// A recovery phrase did not encode a 256-bit (24-word) identity seed.
    case notAnIdentitySeed(Int)
}

/// BIP-39 mnemonic codec over the official English wordlist. Converts between
/// entropy, the human-writable recovery phrase, and the derived seed.
///
/// This is the real, standard BIP-39 algorithm, verified against the official
/// Trezor test vectors (see `fixtures/bip39/vectors.json`) — a phrase produced
/// here restores on any conformant BIP-39 wallet, and every AetherNet language
/// SDK reproduces the same words and seed byte-for-byte.
///
/// ```
///   entropy (16..32 bytes, multiple of 4)  --entropyToMnemonic-->  phrase
///   phrase  --mnemonicToEntropy-->  entropy      (SHA-256 checksum enforced)
///   phrase  --mnemonicToSeed-->  64-byte seed     (PBKDF2-HMAC-SHA512, 2048 rounds)
/// ```
public enum Bip39 {
    private static let pbkdfIterations = 2048
    private static let seedLengthBytes = 64

    // word -> index, built once from the embedded official wordlist.
    private static let wordIndex: [String: Int] = {
        var map = [String: Int](minimumCapacity: Bip39Wordlist.words.count)
        for (i, w) in Bip39Wordlist.words.enumerated() { map[w] = i }
        return map
    }()

    /// Encodes entropy as a BIP-39 mnemonic phrase (single-space-separated words).
    /// - Parameter entropy: 16, 20, 24, 28, or 32 bytes (128..256 bits).
    /// - Throws: `Bip39Error.invalidEntropyLength` if the length is out of range.
    public static func entropyToMnemonic(_ entropy: Data) throws -> String {
        guard entropy.count >= 16, entropy.count <= 32, entropy.count % 4 == 0 else {
            throw Bip39Error.invalidEntropyLength(entropy.count)
        }

        let entropyBytes = [UInt8](entropy)
        let entBits = entropyBytes.count * 8
        let csBits = entBits / 32                                   // 4..8 checksum bits
        let checksum = [UInt8](SHA256.hash(data: entropy))[0]       // only the top csBits are used

        // Read the big-endian bit stream entropy||checksum in 11-bit groups.
        let wordCount = (entBits + csBits) / 11
        var words = [String]()
        words.reserveCapacity(wordCount)

        for w in 0..<wordCount {
            var index = 0
            for b in 0..<11 {
                let bitPos = w * 11 + b
                let bit: Int
                if bitPos < entBits {
                    bit = Int((entropyBytes[bitPos >> 3] >> (7 - (bitPos & 7))) & 1)
                } else {
                    bit = Int((checksum >> (7 - (bitPos - entBits))) & 1)
                }
                index = (index << 1) | bit
            }
            words.append(Bip39Wordlist.words[index])
        }

        return words.joined(separator: " ")
    }

    /// Decodes a BIP-39 mnemonic back to its entropy, enforcing the SHA-256
    /// checksum.
    /// - Throws: `Bip39Error.invalidWordCount` for a wrong word count,
    ///   `Bip39Error.unknownWord` for a word not in the wordlist, or
    ///   `Bip39Error.invalidChecksum` for a checksum mismatch — so a mistyped
    ///   phrase is rejected rather than silently yielding the wrong secret.
    public static func mnemonicToEntropy(_ mnemonic: String) throws -> Data {
        let words = splitWords(mnemonic)
        guard [12, 15, 18, 21, 24].contains(words.count) else {
            throw Bip39Error.invalidWordCount(words.count)
        }

        let totalBits = words.count * 11
        let csBits = totalBits / 33
        let entBits = totalBits - csBits
        var entropy = [UInt8](repeating: 0, count: entBits / 8)
        var actualChecksum = 0

        for (w, word) in words.enumerated() {
            guard let index = wordIndex[word] else {
                throw Bip39Error.unknownWord(word)
            }
            for b in 0..<11 {
                let bit = (index >> (10 - b)) & 1
                let bitPos = w * 11 + b
                if bitPos < entBits {
                    entropy[bitPos >> 3] |= UInt8(bit << (7 - (bitPos & 7)))
                } else {
                    actualChecksum = (actualChecksum << 1) | bit
                }
            }
        }

        let fullChecksum = Int([UInt8](SHA256.hash(data: Data(entropy)))[0])
        let expectedChecksum = fullChecksum >> (8 - csBits)
        guard actualChecksum == expectedChecksum else {
            throw Bip39Error.invalidChecksum
        }

        return Data(entropy)
    }

    /// Derives the 64-byte BIP-39 seed from a mnemonic and optional passphrase,
    /// using PBKDF2-HMAC-SHA512 with 2048 iterations and salt "mnemonic"+passphrase.
    /// Both inputs are NFKD-normalized per the spec.
    public static func mnemonicToSeed(_ mnemonic: String, passphrase: String = "") -> Data {
        let normalizedMnemonic =
            splitWords(mnemonic).joined(separator: " ").decomposedStringWithCompatibilityMapping
        let salt = ("mnemonic" + passphrase).decomposedStringWithCompatibilityMapping

        return pbkdf2SHA512(
            password: Data(normalizedMnemonic.utf8),
            salt: Data(salt.utf8),
            iterations: pbkdfIterations,
            derivedKeyLength: seedLengthBytes
        )
    }

    /// Returns true if `mnemonic` is a well-formed BIP-39 phrase with a valid checksum.
    public static func isValid(_ mnemonic: String) -> Bool {
        do {
            _ = try mnemonicToEntropy(mnemonic)
            return true
        } catch {
            return false
        }
    }

    // MARK: - Internals

    /// Splits on any run of Unicode whitespace, dropping empty entries — matches
    /// C#'s `string.Split(null, RemoveEmptyEntries)`.
    private static func splitWords(_ mnemonic: String) -> [String] {
        mnemonic.split(whereSeparator: { $0.isWhitespace }).map(String.init)
    }

    /// PBKDF2-HMAC-SHA512 (RFC 8018 / PKCS #5 §5.2), implemented over swift-crypto's
    /// `HMAC<SHA512>`. swift-crypto (unlike Apple's CommonCrypto) exposes no PBKDF2
    /// primitive, so we implement the standard construction directly; this keeps the
    /// default `swift build`/`swift test` dependency-free and identical across
    /// platforms. Fixed 64-byte output (one full SHA-512 block), so exactly one
    /// block (i = 1) is produced — no truncation, no second block.
    private static func pbkdf2SHA512(
        password: Data,
        salt: Data,
        iterations: Int,
        derivedKeyLength: Int
    ) -> Data {
        let hLen = 64  // SHA-512 output size in bytes
        let key = SymmetricKey(data: password)
        let blockCount = (derivedKeyLength + hLen - 1) / hLen

        var derivedKey = Data()
        derivedKey.reserveCapacity(blockCount * hLen)

        for block in 1...blockCount {
            // U1 = HMAC(password, salt || INT_32_BE(block))
            var blockIndex = salt
            var be = UInt32(block).bigEndian
            withUnsafeBytes(of: &be) { blockIndex.append(contentsOf: $0) }

            var u = [UInt8](HMAC<SHA512>.authenticationCode(for: blockIndex, using: key))
            var t = u  // T = U1

            // U2..Uc, XOR-accumulated into T
            if iterations > 1 {
                for _ in 2...iterations {
                    u = [UInt8](HMAC<SHA512>.authenticationCode(for: Data(u), using: key))
                    for i in 0..<hLen { t[i] ^= u[i] }
                }
            }

            derivedKey.append(contentsOf: t)
        }

        return Data(derivedKey.prefix(derivedKeyLength))
    }
}

/// Recovery-phrase backup and restore for an AetherNet identity.
///
/// An AetherNet identity is an Ed25519 key pair whose private key is a 32-byte
/// seed — exactly 256 bits, which map cleanly onto a 24-word BIP-39 phrase. The
/// user writes down 24 ordinary words; from those words alone the identity is
/// fully reconstructed on any device. No server, no account, no custodian holds
/// anything — the phrase *is* the identity.
public enum IdentityBackup {
    /// Produces the 24-word recovery phrase for an identity's private key.
    /// - Parameter ed25519PrivateKey: The 32-byte Ed25519 private seed (as
    ///   returned by `Ed25519Service.generateKeyPair`).
    /// - Throws: `Bip39Error.invalidEntropyLength` if the key is not 32 bytes.
    public static func toRecoveryPhrase(_ ed25519PrivateKey: Data) throws -> String {
        guard ed25519PrivateKey.count == 32 else {
            throw Bip39Error.invalidEntropyLength(ed25519PrivateKey.count)
        }
        return try Bip39.entropyToMnemonic(ed25519PrivateKey)
    }

    /// Restores a full identity key pair from a 24-word recovery phrase. The
    /// BIP-39 checksum is enforced, so a mistyped word is rejected rather than
    /// silently reconstructing a different identity.
    /// - Returns: A tuple of (privateKey: 32-byte seed, publicKey: 32-byte point),
    ///   matching `Ed25519Service.generateKeyPair`.
    /// - Throws: `Bip39Error` if the phrase is malformed, fails its checksum, or
    ///   does not encode a 256-bit (24-word) identity seed.
    public static func fromRecoveryPhrase(
        _ recoveryPhrase: String
    ) throws -> (privateKey: Data, publicKey: Data) {
        let privateKey = try Bip39.mnemonicToEntropy(recoveryPhrase)
        guard privateKey.count == 32 else {
            throw Bip39Error.notAnIdentitySeed(privateKey.count)
        }

        // Derive the Ed25519 public key from the 32-byte seed (CryptoKit /
        // swift-crypto: Curve25519 signing key from raw seed).
        let signingKey = try Curve25519.Signing.PrivateKey(rawRepresentation: privateKey)
        let publicKey = Data(signingKey.publicKey.rawRepresentation)
        return (privateKey, publicKey)
    }
}

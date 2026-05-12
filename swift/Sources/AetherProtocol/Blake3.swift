// SPDX-License-Identifier: MIT
// BLAKE3 hash function — pure Swift implementation.
//
// Wire-compatible with the C reference at:
//   https://github.com/BLAKE3-team/BLAKE3/blob/master/reference_impl/reference_impl.rs
//
// Tested against the official BLAKE3 test vectors:
//   empty  → af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9d9ce54814ad88
//   "abc"  → 6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85
//
// Public API:
//   blake3Hash(_ data: Data) -> Data     — 32-byte hash

import Foundation

// MARK: — Public entry point

/// Returns the 32-byte BLAKE3 hash of `data`.
/// Output is identical to `blake3::hash(&data)` in Rust and `blake3.Sum256(data)` in Go.
public func blake3Hash(_ data: Data) -> Data {
    _b3Hash(input: [UInt8](data))
}

// MARK: — Algorithm constants

private let B3_BLOCK_LEN  = 64
private let B3_CHUNK_LEN  = 1024

private let B3_CHUNK_START: UInt32 = 1 << 0
private let B3_CHUNK_END:   UInt32 = 1 << 1
private let B3_PARENT:      UInt32 = 1 << 2
private let B3_ROOT:        UInt32 = 1 << 3

private let B3_IV: [UInt32] = [
    0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
    0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
]

// Message-word permutation for rounds 0–6.
private let B3_MSG_SCHEDULE: [[Int]] = [
    [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
    [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8],
    [3, 4, 10, 12, 13, 2, 7, 14, 6, 5, 9, 0, 11, 15, 8, 1],
    [10, 7, 12, 9, 14, 3, 13, 15, 4, 0, 11, 2, 5, 8, 1, 6],
    [12, 13, 9, 11, 15, 10, 14, 8, 7, 2, 5, 3, 0, 1, 6, 4],
    [9, 14, 11, 5, 8, 12, 15, 1, 13, 3, 0, 10, 2, 6, 4, 7],
    [11, 15, 5, 0, 1, 9, 8, 6, 14, 10, 2, 12, 3, 4, 7, 13],
]

// MARK: — Quarter-round G function

@inline(__always)
private func _b3G(
    _ s: inout [UInt32], _ a: Int, _ b: Int, _ c: Int, _ d: Int,
    _ x: UInt32, _ y: UInt32
) {
    s[a] = s[a] &+ s[b] &+ x; s[d] = (s[d] ^ s[a])._b3RotR(16)
    s[c] = s[c] &+ s[d];       s[b] = (s[b] ^ s[c])._b3RotR(12)
    s[a] = s[a] &+ s[b] &+ y; s[d] = (s[d] ^ s[a])._b3RotR(8)
    s[c] = s[c] &+ s[d];       s[b] = (s[b] ^ s[c])._b3RotR(7)
}

private extension UInt32 {
    @inline(__always)
    func _b3RotR(_ n: Int) -> UInt32 { (self >> n) | (self << (32 &- n)) }
}

// MARK: — Core compression function

/// Compresses one 64-byte block. Returns 16 output words (64 bytes).
/// The chaining value for the next step is `out[0..<8]`.
private func _b3Compress(
    cv: [UInt32], msg: [UInt32],
    counter: UInt64, blockLen: UInt32, flags: UInt32
) -> [UInt32] {
    var s: [UInt32] = [
        cv[0], cv[1], cv[2], cv[3], cv[4], cv[5], cv[6], cv[7],
        B3_IV[0], B3_IV[1], B3_IV[2], B3_IV[3],
        UInt32(truncatingIfNeeded: counter),
        UInt32(counter >> 32),
        blockLen, flags,
    ]
    for r in 0 ..< 7 {
        let sc = B3_MSG_SCHEDULE[r]
        _b3G(&s, 0, 4,  8, 12, msg[sc[0]], msg[sc[1]])
        _b3G(&s, 1, 5,  9, 13, msg[sc[2]], msg[sc[3]])
        _b3G(&s, 2, 6, 10, 14, msg[sc[4]], msg[sc[5]])
        _b3G(&s, 3, 7, 11, 15, msg[sc[6]], msg[sc[7]])
        _b3G(&s, 0, 5, 10, 15, msg[sc[8]], msg[sc[9]])
        _b3G(&s, 1, 6, 11, 12, msg[sc[10]], msg[sc[11]])
        _b3G(&s, 2, 7,  8, 13, msg[sc[12]], msg[sc[13]])
        _b3G(&s, 3, 4,  9, 14, msg[sc[14]], msg[sc[15]])
    }
    for i in 0 ..< 8 { s[i] ^= s[i + 8]; s[i + 8] ^= cv[i] }
    return s
}

// MARK: — Byte / word conversion helpers

/// Reads up to 16 words (64 bytes) from `input` starting at `offset`.
/// Bytes beyond `offset + len` are zero.
private func _b3Words(_ input: [UInt8], offset: Int, len: Int) -> [UInt32] {
    let end = min(offset + len, input.count)
    return (0 ..< 16).map { i in
        let b = offset + i * 4
        var w: UInt32 = 0
        if b     < end { w |= UInt32(input[b])     }
        if b + 1 < end { w |= UInt32(input[b + 1]) << 8 }
        if b + 2 < end { w |= UInt32(input[b + 2]) << 16 }
        if b + 3 < end { w |= UInt32(input[b + 3]) << 24 }
        return w
    }
}

/// Converts 8 UInt32 words to 32 bytes (little-endian).
private func _b3DataFromCV(_ cv: [UInt32]) -> Data {
    var out = Data(count: 32)
    for i in 0 ..< 8 {
        out[i * 4 + 0] = UInt8((cv[i])       & 0xFF)
        out[i * 4 + 1] = UInt8((cv[i] >>  8) & 0xFF)
        out[i * 4 + 2] = UInt8((cv[i] >> 16) & 0xFF)
        out[i * 4 + 3] = UInt8((cv[i] >> 24) & 0xFF)
    }
    return out
}

// MARK: — Chunk compression

/// Compresses a single block within a chunk. Returns the new 8-word chaining value.
private func _b3CompressBlock(
    cv: [UInt32], input: [UInt8], blockOffset: Int, blockLen: Int,
    chunkCounter: UInt64, flags: UInt32
) -> [UInt32] {
    let msg = _b3Words(input, offset: blockOffset, len: blockLen)
    let out = _b3Compress(cv: cv, msg: msg, counter: chunkCounter,
                          blockLen: UInt32(blockLen), flags: flags)
    return Array(out[0 ..< 8])
}

/// Compresses all blocks of a chunk (without ROOT flag).
/// `offset + len` must be ≤ `input.count`; `len` must be > 0.
private func _b3CompressChunk(
    input: [UInt8], offset: Int, len: Int,
    key: [UInt32], counter: UInt64
) -> [UInt32] {
    var cv   = key
    var off  = offset
    var rem  = len
    var first = true

    while rem > B3_BLOCK_LEN {
        cv = _b3CompressBlock(cv: cv, input: input, blockOffset: off,
                              blockLen: B3_BLOCK_LEN, chunkCounter: counter,
                              flags: first ? B3_CHUNK_START : 0)
        off += B3_BLOCK_LEN; rem -= B3_BLOCK_LEN; first = false
    }
    // Last block: CHUNK_START (if this is also the first) + CHUNK_END
    let fl: UInt32 = (first ? B3_CHUNK_START : 0) | B3_CHUNK_END
    return _b3CompressBlock(cv: cv, input: input, blockOffset: off,
                            blockLen: rem, chunkCounter: counter, flags: fl)
}

/// Compresses the last (or only) chunk WITH the ROOT flag, returning 32 bytes.
private func _b3CompressSingleOrLastChunk(
    input: [UInt8], offset: Int, len: Int,
    key: [UInt32], counter: UInt64
) -> Data {
    var cv    = key
    var off   = offset
    var rem   = len
    var first = true

    // Process all but the last block (no ROOT on intermediate blocks)
    while rem > B3_BLOCK_LEN {
        cv = _b3CompressBlock(cv: cv, input: input, blockOffset: off,
                              blockLen: B3_BLOCK_LEN, chunkCounter: counter,
                              flags: first ? B3_CHUNK_START : 0)
        off += B3_BLOCK_LEN; rem -= B3_BLOCK_LEN; first = false
    }
    // Last block: CHUNK_START|CHUNK_END|ROOT (if first) else CHUNK_END|ROOT
    let fl: UInt32 = (first ? B3_CHUNK_START : 0) | B3_CHUNK_END | B3_ROOT
    let msg = _b3Words(input, offset: off, len: rem)
    let out = _b3Compress(cv: cv, msg: msg, counter: counter,
                          blockLen: UInt32(rem), flags: fl)
    return _b3DataFromCV(Array(out[0 ..< 8]))
}

// MARK: — Parent node compression

private func _b3ParentCV(left: [UInt32], right: [UInt32], key: [UInt32]) -> [UInt32] {
    let msg = left + right
    let out = _b3Compress(cv: key, msg: msg, counter: 0,
                          blockLen: UInt32(B3_BLOCK_LEN), flags: B3_PARENT)
    return Array(out[0 ..< 8])
}

private func _b3RootOutput(left: [UInt32], right: [UInt32], key: [UInt32]) -> Data {
    let msg = left + right
    let out = _b3Compress(cv: key, msg: msg, counter: 0,
                          blockLen: UInt32(B3_BLOCK_LEN), flags: B3_PARENT | B3_ROOT)
    return _b3DataFromCV(Array(out[0 ..< 8]))
}

// MARK: — Main BLAKE3 hash

private func _b3Hash(input: [UInt8]) -> Data {
    let key   = B3_IV
    var stack = [[UInt32]]()
    var chunkCounter: UInt64 = 0
    var pos = 0

    // Process all complete chunks (all but the last), without ROOT flag
    while pos + B3_CHUNK_LEN < input.count {
        let cv = _b3CompressChunk(input: input, offset: pos, len: B3_CHUNK_LEN,
                                  key: key, counter: chunkCounter)
        // Push into binary tree, merging whenever trailing-zero count demands it
        var merged = cv
        var count  = chunkCounter + 1
        while count & 1 == 0 {
            merged = _b3ParentCV(left: stack.removeLast(), right: merged, key: key)
            count >>= 1
        }
        stack.append(merged)
        chunkCounter += 1
        pos += B3_CHUNK_LEN
    }

    // Last (or only) chunk — must include ROOT
    let lastLen = input.count - pos  // may be 0 for empty input

    if stack.isEmpty {
        // Single-chunk path: this chunk is the root
        return _b3CompressSingleOrLastChunk(input: input, offset: pos, len: lastLen,
                                            key: key, counter: chunkCounter)
    }

    // Multi-chunk path: last chunk CV without ROOT, then merge and apply ROOT
    let lastCV = _b3CompressChunk(input: input, offset: pos, len: lastLen,
                                  key: key, counter: chunkCounter)

    var right = lastCV
    while stack.count > 1 {
        right = _b3ParentCV(left: stack.removeLast(), right: right, key: key)
    }
    return _b3RootOutput(left: stack.removeLast(), right: right, key: key)
}

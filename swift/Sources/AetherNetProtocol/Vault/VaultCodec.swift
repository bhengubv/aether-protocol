// SPDX-License-Identifier: MIT
//
// File-level helpers over ReedSolomonCodec: split a plaintext blob into K systematic data shards
// (zero-padded), produce the full N-shard set, and reconstruct the original blob from any K surviving
// shards. Byte-identical to the C# vault data layout: shardSize = ceil(size/K), data shard i is
// plaintext[i*shardSize .. (i+1)*shardSize] zero-padded, and recovery concatenates the K recovered
// data shards in index order then trims to the original size.

import Foundation

public extension ReedSolomonCodec {

    /// Slice `data` into K equal zero-padded data shards of length `shardSize = ceil(count/K)`. This
    /// is the systematic prefix the encoder leaves unchanged.
    static func splitIntoDataShards(_ data: [UInt8], k: Int) throws -> [[UInt8]] {
        if k < 1 { throw ReedSolomonError.invalidParameters("K must be >= 1.") }
        if data.isEmpty { throw ReedSolomonError.invalidShards("data must not be empty.") }
        let shardSize = (data.count + k - 1) / k
        var shards = [[UInt8]](repeating: [], count: k)
        for i in 0..<k {
            var shard = [UInt8](repeating: 0, count: shardSize)
            let offset = i * shardSize
            if offset < data.count {
                let length = min(shardSize, data.count - offset)
                for j in 0..<length { shard[j] = data[offset + j] }
            }
            shards[i] = shard
        }
        return shards
    }

    /// Split `data` into K systematic data shards and return the full set of N = K+M shards.
    func encodeData(_ data: [UInt8]) throws -> [[UInt8]] {
        let split = try ReedSolomonCodec.splitIntoDataShards(data, k: dataShards)
        return try encode(split)
    }

    /// Reconstruct the original blob of `originalSize` bytes from any K surviving shards. `available`
    /// maps a shard index (0…N-1) to its bytes. Throws if fewer than K shards are supplied.
    func reconstructData(_ available: [Int: [UInt8]], originalSize: Int) throws -> [UInt8] {
        let recoveredShards = try decodeDataShards(available)
        if originalSize < 0 {
            throw ReedSolomonError.invalidShards("originalSize must be >= 0.")
        }

        let k = dataShards
        let shardSize = recoveredShards[0].count
        var out = [UInt8](repeating: 0, count: k * shardSize)
        for j in 0..<k {
            let base = j * shardSize
            let shard = recoveredShards[j]
            for i in 0..<shardSize { out[base + i] = shard[i] }
        }
        if originalSize > out.count {
            throw ReedSolomonError.unrecoverable("originalSize exceeds reconstructed length.")
        }
        return Array(out[0..<originalSize])
    }
}

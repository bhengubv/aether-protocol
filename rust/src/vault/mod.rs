// SPDX-License-Identifier: MIT

//! Vault — production systematic Cauchy-Reed-Solomon (K, M) erasure coding over
//! GF(2^8) (primitive polynomial 0x11D, alpha = 2).
//!
//! Rust port of `AetherNet.Vault.ReedSolomonCodec` (and the Go `vault` package).
//! A blob is split into K systematic data shards (the plaintext partitioned
//! into equal zero-padded slices) plus M Cauchy-Reed-Solomon parity shards; any
//! K of the N = K+M shards reconstruct the original, byte-for-byte identical to
//! every other language implementation. K-1 or fewer shards is unrecoverable.
//!
//! See [`ReedSolomonCodec`] for the codec and [`split_into_data_shards`] /
//! [`ReedSolomonCodec::encode_data`] / [`ReedSolomonCodec::reconstruct_data`]
//! for the file-level helpers.

pub mod reed_solomon;
pub mod vault_codec;

pub use reed_solomon::ReedSolomonCodec;
pub use vault_codec::split_into_data_shards;

use thiserror::Error;

/// Errors raised by the vault erasure codec.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum VaultError {
    /// A constructor or encode argument was invalid (bad K/M, mismatched shard lengths, …).
    #[error("vault: invalid parameters: {0}")]
    InvalidParameters(String),

    /// Decoding is impossible with the supplied shards (fewer than K survivors, or a singular
    /// generator submatrix). The fixture's `should_fail` case (K-1 survivors) lands here.
    #[error("vault: unrecoverable: {0}")]
    Unrecoverable(String),
}

// SPDX-License-Identifier: MIT

//! Rendezvous derivation: two phones agreeing where to meet from their tags alone, before either
//! radio has done anything. Port of the C# reference `AetherNet.Rendezvous`
//! (src/AetherNet.Core/Rendezvous/). Verified byte-for-byte against
//! `fixtures/meeting/meeting_basic.json`.

use hkdf::Hkdf;
use sha2::{Digest, Sha256};
use uuid::Uuid;

/// Ties this derivation to this purpose, so the same tags used elsewhere yield nothing here.
const INFO: &[u8] = b"aether-meeting-v1";

/// Crockford's alphabet: no I, L, O or U, so it cannot be misread down a phone line.
const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/// How many characters a rendezvous carries — longer than the widest radio needs.
pub const LENGTH: usize = 25;

/// Reports whether `my_tag` hosts the group it would share with `their_tag`: order the two tags and
/// the ordinally-lower one hosts. A missing tag hosts nothing.
pub fn hosts_the_group(my_tag: &str, their_tag: &str) -> bool {
    if my_tag.is_empty() || their_tag.is_empty() {
        return false;
    }
    my_tag < their_tag
}

/// A meeting point derived from two tags: who you are meeting, where, and which of you opens.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Meeting {
    /// Whose tag this meeting is with.
    pub peer_tag: String,
    /// Where to meet — letters and digits, the same on both phones.
    pub rendezvous: String,
    /// Whether this phone is the one that opens (its own tag is the ordinally-lower one).
    pub i_start: bool,
}

impl Meeting {
    /// Works out where two phones meet, from their tags alone. Returns `None` when either tag is
    /// missing or blank, or they are the same phone (tags are case-insensitive, so two case-variants
    /// are one identity and do not meet).
    pub fn with(my_tag: &str, their_tag: &str) -> Option<Meeting> {
        if my_tag.trim().is_empty() || their_tag.trim().is_empty() {
            return None;
        }
        if my_tag.eq_ignore_ascii_case(their_tag) {
            return None;
        }

        // Ordered, so both phones feed the derivation the same bytes in the same order.
        let (first, second) = if my_tag < their_tag {
            (my_tag, their_tag)
        } else {
            (their_tag, my_tag)
        };

        // None salt matches the C# reference's ReadOnlySpan<byte>::Empty — the same choice the erid
        // port makes; empty and absent salt are equivalent in HKDF.
        let hk = Hkdf::<Sha256>::new(None, format!("{first}\n{second}").as_bytes());
        let mut derived = [0u8; 16];
        hk.expand(INFO, &mut derived)
            .expect("16 is a valid HKDF-SHA256 output length");

        Some(Meeting {
            peer_tag: their_tag.to_owned(),
            rendezvous: encode(&derived)[..LENGTH].to_owned(),
            i_start: hosts_the_group(my_tag, their_tag),
        })
    }

    /// As much of the rendezvous as a radio can use, from the front (C# `Where`).
    pub fn prefix(&self, characters: usize) -> &str {
        if characters >= self.rendezvous.len() {
            &self.rendezvous
        } else {
            &self.rendezvous[..characters]
        }
    }

    /// The meeting as a UUID, for a radio that finds people by advertising one.
    ///
    /// Built to match the .NET reference: the raw hash bytes carry the version/variant, and the 16
    /// bytes are .NET's `Guid::ToByteArray()` layout (first three groups little-endian), so
    /// `from_bytes_le` makes both the string and `to_bytes_le()` agree with C#.
    pub fn uuid(&self) -> Uuid {
        let mut digest = Sha256::new();
        digest.update(INFO);
        digest.update(b"-uuid\n");
        digest.update(self.rendezvous.as_bytes());
        let hash = digest.finalize();

        let mut b = [0u8; 16];
        b.copy_from_slice(&hash[..16]);
        b[7] = (b[7] & 0x0F) | 0x40; // version 4
        b[8] = (b[8] & 0x3F) | 0x80; // variant 1
        Uuid::from_bytes_le(b)
    }

    /// The meeting as a small number, for a radio whose address space is tiny (`bits` in `1..=32`).
    pub fn address(&self, bits: u32) -> u32 {
        assert!((1..=32).contains(&bits), "bits must be between 1 and 32");

        let mut digest = Sha256::new();
        digest.update(INFO);
        digest.update(b"-addr\n");
        digest.update(self.rendezvous.as_bytes());
        let hash = digest.finalize();

        let whole = u32::from_be_bytes([hash[0], hash[1], hash[2], hash[3]]);
        if bits == 32 {
            whole
        } else {
            whole & ((1u32 << bits) - 1)
        }
    }
}

/// Renders bytes as Crockford base32, five bits at a time — the same bit walk as the reference.
fn encode(data: &[u8]) -> String {
    let total = data.len() * 8 / 5;
    let mut out = String::with_capacity(total);
    let mut bit = 0usize;
    for _ in 0..total {
        let mut value = 0usize;
        for _ in 0..5 {
            let source = data[bit / 8];
            let taken = (source >> (7 - (bit % 8))) & 1;
            value = (value << 1) | usize::from(taken);
            bit += 1;
        }
        out.push(ALPHABET[value] as char);
    }
    out
}

// SPDX-License-Identifier: MIT

//! AetherTag — human-readable, shareable identity address derived from an
//! Ed25519 public key.
//!
//! # Algorithm
//! 1. SHA-256(public_key) → 32-byte hash
//! 2. Extract first 50 bits via bit-packing of bytes 0-6
//! 3. Encode as 10 Crockford base-32 characters (5 bits each)
//! 4. Format as "XXXXX-XXXXX"
//!
//! # Crockford base-32 alphabet
//! `0123456789ABCDEFGHJKMNPQRSTVWXYZ` (removes I, L, O, U)

use sha2::{Digest, Sha256};

/// Crockford base-32 alphabet (32 characters, no I/L/O/U).
const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/// Expected total length of a formatted tag including the separator ("XXXXX-XXXXX").
const TAG_LEN: usize = 11;

/// Length of each half of the tag (5 characters per group).
const HALF_LEN: usize = 5;

/// A human-readable, shareable identity address for an Aether node.
///
/// The tag is derived deterministically from the node's 32-byte Ed25519
/// public key. Equal public keys always produce equal tags; different public
/// keys almost certainly produce different tags (50-bit address space ≈ 1 in
/// 10¹⁵ collision probability).
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct AetherTag {
    /// Always stored in canonical form: upper-case with separator (e.g. "KXJB7-MN2P4").
    value: String,
}

impl AetherTag {
    // -----------------------------------------------------------------------
    // Constructors
    // -----------------------------------------------------------------------

    /// Derive an `AetherTag` from a 32-byte Ed25519 public key.
    ///
    /// # Errors
    /// Returns [`AetherTagError::InvalidKeyLength`] if `public_key` is not
    /// exactly 32 bytes.
    pub fn from_public_key(public_key: &[u8]) -> Result<AetherTag, AetherTagError> {
        if public_key.len() != 32 {
            return Err(AetherTagError::InvalidKeyLength);
        }

        let hash = Sha256::digest(public_key);
        let value = encode_tag(&hash);

        Ok(AetherTag { value })
    }

    /// Parse a tag string into an `AetherTag`.
    ///
    /// Accepts:
    /// - Canonical form: `"KXJB7-MN2P4"` (upper-case, with separator)
    /// - Without separator: `"KXJB7MN2P4"`
    /// - Lower-case or mixed-case variants of the above
    ///
    /// # Errors
    /// - [`AetherTagError::InvalidFormat`] — wrong length or wrong separator position
    /// - [`AetherTagError::InvalidCharacter`] — character outside Crockford alphabet
    pub fn parse(tag: &str) -> Result<AetherTag, AetherTagError> {
        let upper = tag.to_uppercase();
        let normalized = normalize_tag(&upper)?;
        Ok(AetherTag { value: normalized })
    }

    /// Like [`parse`] but returns `None` instead of an error.
    pub fn try_parse(tag: &str) -> Option<AetherTag> {
        Self::parse(tag).ok()
    }

    // -----------------------------------------------------------------------
    // Verification
    // -----------------------------------------------------------------------

    /// Verify that `tag` was derived from `public_key`.
    ///
    /// Returns `false` for any parse or key error rather than propagating.
    pub fn verify(tag: &str, public_key: &[u8]) -> bool {
        let Ok(parsed) = Self::parse(tag) else {
            return false;
        };
        let Ok(expected) = Self::from_public_key(public_key) else {
            return false;
        };
        parsed == expected
    }

    // -----------------------------------------------------------------------
    // Accessors
    // -----------------------------------------------------------------------

    /// Return the canonical tag string (e.g. `"KXJB7-MN2P4"`).
    pub fn value(&self) -> &str {
        &self.value
    }

    /// Returns `true` — a well-formed `AetherTag` is always valid by
    /// construction; this method exists for interface completeness and
    /// future extensibility.
    pub fn is_valid(&self) -> bool {
        // Tags are validated at construction time; structural invariant holds.
        self.value.len() == TAG_LEN
            && self.value.as_bytes()[HALF_LEN] == b'-'
            && self.value[..HALF_LEN]
                .bytes()
                .chain(self.value[HALF_LEN + 1..].bytes())
                .all(|b| ALPHABET.contains(&b))
    }
}

impl std::fmt::Display for AetherTag {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.value)
    }
}

// ---------------------------------------------------------------------------
// Error type
// ---------------------------------------------------------------------------

/// Errors that can occur when working with [`AetherTag`].
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum AetherTagError {
    /// The public key supplied to [`AetherTag::from_public_key`] was not
    /// exactly 32 bytes.
    #[error("public key must be exactly 32 bytes")]
    InvalidKeyLength,

    /// The tag string had an incorrect length or a misplaced (or missing)
    /// separator.
    #[error("tag has invalid format — expected XXXXX-XXXXX (11 chars) or XXXXXXXXXX (10 chars)")]
    InvalidFormat,

    /// The tag string contained a character outside the Crockford base-32
    /// alphabet (`0-9`, `A-H`, `J`, `K`, `M`, `N`, `P-T`, `V-Z`).
    #[error("tag contains an invalid character")]
    InvalidCharacter,
}

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/// Encode a SHA-256 hash as a canonical "XXXXX-XXXXX" tag.
fn encode_tag(hash: &[u8]) -> String {
    // Pack bytes 0-6 into a u64 capturing 50 bits.
    //
    // Bit layout (high to low in the u64):
    //   bits 49-42 : hash[0]
    //   bits 41-34 : hash[1]
    //   bits 33-26 : hash[2]
    //   bits 25-18 : hash[3]
    //   bits 17-10 : hash[4]
    //   bits  9- 2 : hash[5]
    //   bits  1- 0 : top 2 bits of hash[6]  (hash[6] >> 6)
    let bits: u64 = ((hash[0] as u64) << 42)
        | ((hash[1] as u64) << 34)
        | ((hash[2] as u64) << 26)
        | ((hash[3] as u64) << 18)
        | ((hash[4] as u64) << 10)
        | ((hash[5] as u64) << 2)
        | ((hash[6] >> 6) as u64);

    // Extract 10 groups of 5 bits each, most-significant first.
    let mut chars = [0u8; 10];
    for i in 0..10 {
        let shift = 45 - i * 5; // positions: 45, 40, 35, …, 0
        let index = ((bits >> shift) & 0x1F) as usize;
        chars[i] = ALPHABET[index];
    }

    // Format as "XXXXX-XXXXX".
    let mut s = String::with_capacity(TAG_LEN);
    s.push_str(std::str::from_utf8(&chars[..5]).unwrap());
    s.push('-');
    s.push_str(std::str::from_utf8(&chars[5..]).unwrap());
    s
}

/// Validate and normalise a tag string that has already been upper-cased.
///
/// Accepts both "XXXXX-XXXXX" (11 chars) and "XXXXXXXXXX" (10 chars).
/// Returns the canonical "XXXXX-XXXXX" form.
fn normalize_tag(upper: &str) -> Result<String, AetherTagError> {
    let stripped = match upper.len() {
        TAG_LEN => {
            // Must have separator at position 5.
            if upper.as_bytes()[HALF_LEN] != b'-' {
                return Err(AetherTagError::InvalidFormat);
            }
            // Remove the separator for validation.
            let first = &upper[..HALF_LEN];
            let second = &upper[HALF_LEN + 1..];
            format!("{}{}", first, second)
        }
        10 => upper.to_string(),
        _ => return Err(AetherTagError::InvalidFormat),
    };

    // Validate each character against the Crockford alphabet.
    for b in stripped.bytes() {
        if !ALPHABET.contains(&b) {
            return Err(AetherTagError::InvalidCharacter);
        }
    }

    // Re-insert the separator.
    Ok(format!("{}-{}", &stripped[..5], &stripped[5..]))
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    // -----------------------------------------------------------------------
    // Helper: a deterministic 32-byte key
    // -----------------------------------------------------------------------

    /// Key whose every byte equals its index (0x00, 0x01, …, 0x1F).
    fn sequential_key() -> Vec<u8> {
        (0u8..32).collect()
    }

    /// Key filled with 0xAB bytes.
    fn ab_key() -> Vec<u8> {
        vec![0xABu8; 32]
    }

    // -----------------------------------------------------------------------
    // Spec known-vector: key [0x01..=0x20] must produce "NRGPR-BQN4H"
    // -----------------------------------------------------------------------

    #[test]
    fn known_vector_0x01_to_0x20() {
        let key: Vec<u8> = (0x01u8..=0x20u8).collect();
        let tag = AetherTag::from_public_key(&key).unwrap();
        assert_eq!(
            tag.value(),
            "NRGPR-BQN4H",
            "key [0x01..=0x20] must produce the canonical tag NRGPR-BQN4H"
        );
    }

    // -----------------------------------------------------------------------
    // Known-vector: format invariant
    // -----------------------------------------------------------------------

    #[test]
    fn from_public_key_produces_correct_format() {
        let key = sequential_key();
        let tag = AetherTag::from_public_key(&key).unwrap();
        let v = tag.value();

        // Must be exactly 11 characters.
        assert_eq!(v.len(), TAG_LEN, "tag length should be {TAG_LEN}");

        // Separator at position 5.
        assert_eq!(v.as_bytes()[HALF_LEN], b'-', "separator must be at position 5");

        // Every non-separator character must be in the Crockford alphabet.
        for b in v.bytes() {
            if b != b'-' {
                assert!(
                    ALPHABET.contains(&b),
                    "character '{}' not in Crockford alphabet",
                    b as char
                );
            }
        }
    }

    // -----------------------------------------------------------------------
    // Known-vector: determinism — same key always gives same tag
    // -----------------------------------------------------------------------

    #[test]
    fn from_public_key_is_deterministic() {
        let key = sequential_key();
        let tag1 = AetherTag::from_public_key(&key).unwrap();
        let tag2 = AetherTag::from_public_key(&key).unwrap();
        assert_eq!(tag1, tag2);
    }

    // -----------------------------------------------------------------------
    // Round-trip: from_public_key → to_string → parse → compare
    // -----------------------------------------------------------------------

    #[test]
    fn round_trip_sequential_key() {
        let key = sequential_key();
        let original = AetherTag::from_public_key(&key).unwrap();
        let string = original.to_string();
        let parsed = AetherTag::parse(&string).unwrap();
        assert_eq!(original, parsed);
    }

    #[test]
    fn round_trip_ab_key() {
        let key = ab_key();
        let original = AetherTag::from_public_key(&key).unwrap();
        let parsed = AetherTag::parse(&original.to_string()).unwrap();
        assert_eq!(original, parsed);
    }

    // -----------------------------------------------------------------------
    // verify()
    // -----------------------------------------------------------------------

    #[test]
    fn verify_correct_key_returns_true() {
        let key = sequential_key();
        let tag = AetherTag::from_public_key(&key).unwrap();
        assert!(AetherTag::verify(tag.value(), &key));
    }

    #[test]
    fn verify_wrong_key_returns_false() {
        let key = sequential_key();
        let other_key = ab_key();
        let tag = AetherTag::from_public_key(&key).unwrap();
        assert!(!AetherTag::verify(tag.value(), &other_key));
    }

    #[test]
    fn verify_invalid_tag_string_returns_false() {
        let key = sequential_key();
        assert!(!AetherTag::verify("NOT-ATAG", &key));
    }

    #[test]
    fn verify_wrong_length_key_returns_false() {
        let key = sequential_key();
        let tag = AetherTag::from_public_key(&key).unwrap();
        assert!(!AetherTag::verify(tag.value(), &[0u8; 16]));
    }

    // -----------------------------------------------------------------------
    // parse() — accepted forms
    // -----------------------------------------------------------------------

    #[test]
    fn parse_accepts_canonical_form() {
        let key = sequential_key();
        let canonical = AetherTag::from_public_key(&key).unwrap().to_string();
        assert!(AetherTag::parse(&canonical).is_ok());
    }

    #[test]
    fn parse_accepts_without_separator() {
        let key = sequential_key();
        let canonical = AetherTag::from_public_key(&key).unwrap().to_string();
        // Strip the dash.
        let no_dash: String = canonical.chars().filter(|&c| c != '-').collect();
        let parsed = AetherTag::parse(&no_dash).unwrap();
        assert_eq!(parsed.value(), canonical);
    }

    #[test]
    fn parse_accepts_lowercase() {
        let key = sequential_key();
        let canonical = AetherTag::from_public_key(&key).unwrap().to_string();
        let lower = canonical.to_lowercase();
        let parsed = AetherTag::parse(&lower).unwrap();
        assert_eq!(parsed.value(), canonical);
    }

    #[test]
    fn parse_accepts_mixed_case() {
        let key = sequential_key();
        let canonical = AetherTag::from_public_key(&key).unwrap().to_string();
        // Alternate case character by character.
        let mixed: String = canonical
            .chars()
            .enumerate()
            .map(|(i, c)| if i % 2 == 0 { c.to_ascii_lowercase() } else { c.to_ascii_uppercase() })
            .collect();
        let parsed = AetherTag::parse(&mixed).unwrap();
        assert_eq!(parsed.value(), canonical);
    }

    // -----------------------------------------------------------------------
    // parse() — rejected forms
    // -----------------------------------------------------------------------

    #[test]
    fn parse_rejects_empty_string() {
        assert_eq!(AetherTag::parse(""), Err(AetherTagError::InvalidFormat));
    }

    #[test]
    fn parse_rejects_too_short() {
        assert_eq!(AetherTag::parse("ABCD"), Err(AetherTagError::InvalidFormat));
    }

    #[test]
    fn parse_rejects_too_long() {
        assert_eq!(
            AetherTag::parse("ABCDE-FGHJ1X"),
            Err(AetherTagError::InvalidFormat)
        );
    }

    #[test]
    fn parse_rejects_wrong_separator_position() {
        // 11 chars but separator is not at index 5.
        assert_eq!(
            AetherTag::parse("ABCD-EFGH1X"),
            Err(AetherTagError::InvalidFormat)
        );
    }

    #[test]
    fn parse_rejects_invalid_character_i() {
        // 'I' is not in the Crockford alphabet.
        assert_eq!(
            AetherTag::parse("ABCDI-EFGH1"),
            Err(AetherTagError::InvalidCharacter)
        );
    }

    #[test]
    fn parse_rejects_invalid_character_o() {
        // 'O' is not in the Crockford alphabet.
        assert_eq!(
            AetherTag::parse("OABCD-EFGH1"),
            Err(AetherTagError::InvalidCharacter)
        );
    }

    #[test]
    fn parse_rejects_invalid_character_l() {
        assert_eq!(
            AetherTag::parse("LABCD-EFGH1"),
            Err(AetherTagError::InvalidCharacter)
        );
    }

    #[test]
    fn parse_rejects_invalid_character_u() {
        assert_eq!(
            AetherTag::parse("UABCD-EFGH1"),
            Err(AetherTagError::InvalidCharacter)
        );
    }

    #[test]
    fn parse_rejects_special_characters() {
        // "AB!DE-FGH1X" is 11 chars with separator at position 5 — format is
        // structurally valid, so normalisation proceeds to character validation
        // and correctly rejects '!' as an invalid Crockford character.
        assert_eq!(
            AetherTag::parse("AB!DE-FGH1X"),
            Err(AetherTagError::InvalidCharacter)
        );
    }

    // -----------------------------------------------------------------------
    // try_parse()
    // -----------------------------------------------------------------------

    #[test]
    fn try_parse_returns_some_for_valid_tag() {
        let key = sequential_key();
        let canonical = AetherTag::from_public_key(&key).unwrap().to_string();
        assert!(AetherTag::try_parse(&canonical).is_some());
    }

    #[test]
    fn try_parse_returns_none_for_invalid_tag() {
        assert!(AetherTag::try_parse("").is_none());
        assert!(AetherTag::try_parse("NOT-VALID!!").is_none());
    }

    // -----------------------------------------------------------------------
    // Different keys → different tags
    // -----------------------------------------------------------------------

    #[test]
    fn different_keys_produce_different_tags() {
        let key1 = sequential_key();
        let key2 = ab_key();
        let tag1 = AetherTag::from_public_key(&key1).unwrap();
        let tag2 = AetherTag::from_public_key(&key2).unwrap();
        assert_ne!(tag1, tag2, "distinct public keys must produce distinct tags");
    }

    #[test]
    fn incrementally_different_keys_produce_different_tags() {
        // Even a one-byte difference must (in practice) produce a different tag.
        let mut key_a = sequential_key();
        let mut key_b = sequential_key();
        key_a[0] = 0x00;
        key_b[0] = 0xFF;
        let tag_a = AetherTag::from_public_key(&key_a).unwrap();
        let tag_b = AetherTag::from_public_key(&key_b).unwrap();
        assert_ne!(tag_a, tag_b);
    }

    // -----------------------------------------------------------------------
    // from_public_key() error cases
    // -----------------------------------------------------------------------

    #[test]
    fn from_public_key_rejects_empty_slice() {
        assert_eq!(
            AetherTag::from_public_key(&[]),
            Err(AetherTagError::InvalidKeyLength)
        );
    }

    #[test]
    fn from_public_key_rejects_16_byte_key() {
        assert_eq!(
            AetherTag::from_public_key(&[0u8; 16]),
            Err(AetherTagError::InvalidKeyLength)
        );
    }

    #[test]
    fn from_public_key_rejects_64_byte_key() {
        assert_eq!(
            AetherTag::from_public_key(&[0u8; 64]),
            Err(AetherTagError::InvalidKeyLength)
        );
    }

    // -----------------------------------------------------------------------
    // is_valid()
    // -----------------------------------------------------------------------

    #[test]
    fn is_valid_returns_true_for_constructed_tag() {
        let key = sequential_key();
        let tag = AetherTag::from_public_key(&key).unwrap();
        assert!(tag.is_valid());
    }

    // -----------------------------------------------------------------------
    // Display
    // -----------------------------------------------------------------------

    #[test]
    fn display_matches_value() {
        let key = sequential_key();
        let tag = AetherTag::from_public_key(&key).unwrap();
        assert_eq!(format!("{}", tag), tag.value());
    }
}
